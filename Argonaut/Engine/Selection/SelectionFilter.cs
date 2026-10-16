using System;
using System.Collections.Generic;
using System.IO;
using Argonaut.Engine.Text;

namespace Argonaut.Engine.Selection;

/// <summary>What a filter file's entries count: lines (1-based) or bytes (0-based).</summary>
public enum SelectionUnit
{
    Lines,
    Bytes,
}

/// <summary><see cref="First"/>..<see cref="Last"/> inclusive, in the filter's unit;
/// <see cref="Last"/> is <see cref="long.MaxValue"/> for an entry that runs to the end of the
/// file.</summary>
public readonly record struct SelectionRange(long First, long Last);

/// <summary>An entry of a filter file that could not be read, and the filter file line it is on.</summary>
public sealed class SelectionFilterException(int filterLine, string message)
    : FormatException($"Line {filterLine}: {message}")
{
    /// <summary>The 1-based line of the filter file the entry is on.</summary>
    public int FilterLine { get; } = filterLine;
}

/// <summary>
/// The parts of a file a filter file asks for, in file order: overlapping and touching entries
/// merged, duplicates gone. The order the file lists them in is not kept - a view of them reads
/// the file front to back, which is what keeps it fast and lets a reload keep its place.
///
/// A filter file holds entries written as <c>--lines</c> or <c>--bytes</c> writes a range, plus a
/// single number on its own, separated by commas, newlines or both. Blank lines are skipped and
/// anything after <c>#</c> on a line is a comment. Whether the entries are lines or bytes is not in
/// the file: the command line says which, and the two are never mixed. An entry that cannot be read
/// fails the whole filter, naming the line it is on - a filter half applied is worse than none.
/// </summary>
public sealed class SelectionFilter
{
    private readonly SelectionRange[] ranges;

    private SelectionFilter(SelectionUnit unit, SelectionRange[] ranges, int entryCount)
    {
        Unit = unit;
        this.ranges = ranges;
        EntryCount = entryCount;
    }

    public SelectionUnit Unit { get; }

    /// <summary>The merged ranges, in file order, none overlapping or touching another.</summary>
    public ReadOnlySpan<SelectionRange> Ranges => this.ranges;

    /// <summary>How many entries the filter file held, before merging.</summary>
    public int EntryCount { get; }

    /// <summary>Reads a filter file's entries in <paramref name="unit"/>.</summary>
    /// <exception cref="SelectionFilterException">An entry is not a range.</exception>
    public static SelectionFilter Parse(TextReader reader, SelectionUnit unit)
    {
        var entries = new List<SelectionRange>();
        long lowest = unit == SelectionUnit.Lines ? 1 : 0;
        int filterLine = 0;

        while (reader.ReadLine() is { } line)
        {
            filterLine++;
            var text = line.AsSpan();
            int comment = text.IndexOf('#');
            if (comment >= 0)
                text = text[..comment];

            foreach (var part in text.Split(','))
            {
                var entry = text[part].Trim();
                if (entry.IsEmpty)
                    continue;

                if (RangeSyntax.ParseRange(entry, lowest, allowSingle: true) is not { } range)
                    throw new SelectionFilterException(filterLine, $"\"{entry}\" is not a {(unit == SelectionUnit.Lines ? "line" : "byte")} range.");

                entries.Add(new SelectionRange(range.First, range.Last ?? long.MaxValue));
            }
        }

        return new SelectionFilter(unit, Merge(entries), entries.Count);
    }

    private static SelectionRange[] Merge(List<SelectionRange> entries)
    {
        if (entries.Count == 0)
            return [];

        entries.Sort(static (a, b) => a.First.CompareTo(b.First));

        var merged = new List<SelectionRange>(entries.Count) { entries[0] };
        for (int i = 1; i < entries.Count; i++)
        {
            var last = merged[^1];
            var next = entries[i];

            // Touching counts as overlapping: lines 1-5 and 6-9 are one run of lines 1-9.
            if (last.Last == long.MaxValue || next.First <= last.Last + 1)
                merged[^1] = last with { Last = Math.Max(last.Last, next.Last) };
            else
                merged.Add(next);
        }

        return merged.ToArray();
    }
}
