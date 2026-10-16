using System;
using System.Globalization;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Text;

namespace Argonaut.Engine.Selection;

/// <summary>
/// The parts of a file a filter file selected, opened as one document - what
/// <c>--lines --filter</c> and <c>--bytes --filter</c> open. Like an excerpt it is read where it
/// is in the file rather than copied; unlike one it is many stretches laid end to end, so it reads
/// through <see cref="SelectionByteSource"/> rather than a plain range of <see cref="Whole"/>.
///
/// <see cref="Open"/> maps the whole file once and windows it through the run table.
/// <see cref="OpenRange"/> - a search chunk, an NDJSON line's own document - maps only the span
/// of the file its runs cover, so whoever opens it owns that mapping outright, as search must.
///
/// <see cref="Path"/> is null, as for an excerpt: this is not the file, so nothing path-keyed
/// touches it. Unlike an excerpt it is read-only - no edit, no Save As.
///
/// Owns <see cref="Whole"/>, and disposes it.
/// </summary>
public sealed class SelectionByteOrigin : IByteOrigin
{
    private readonly long[] viewStarts;
    private LineNumbering? numbering;

    public SelectionByteOrigin(IByteOrigin whole, LineSelection selection)
    {
        ArgumentNullException.ThrowIfNull(whole);
        ArgumentNullException.ThrowIfNull(selection);

        Whole = whole;
        Selection = selection;

        var runs = selection.Runs;
        this.viewStarts = new long[runs.Length];
        long view = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            this.viewStarts[i] = view;
            view += runs[i].ViewLength;
        }

        Description = Describe(selection, Exact);
        ShortDescription = Describe(selection, CompactCount.Format);
        DisplayName = $"{whole.DisplayName} ({Description})";
    }

    /// <summary>The document the selection is part of.</summary>
    public IByteOrigin Whole { get; }

    public LineSelection Selection { get; }

    /// <summary>
    /// How the selection's lines are numbered as the file numbers them: each by its file line, or
    /// for a selection of bytes, whose line numbers are not known, each by the file offset it
    /// starts at. Built on first use - a run apiece - and kept.
    /// </summary>
    public LineNumbering Numbering => this.numbering ??= BuildNumbering();

    /// <summary>What is selected: "1,204 lines selected", "12 ranges selected".</summary>
    public string Description { get; }

    /// <summary><see cref="Description"/> with its number rounded to fit a label.</summary>
    public string ShortDescription { get; }

    public string DisplayName { get; }

    /// <summary>Always null - see the class remarks.</summary>
    public string? Path => null;

    public long AvailableLength => Selection.ViewLength;

    public IByteSource Open() => Window(Whole.Open(), 0, AvailableLength, fileBase: 0);

    public IByteSource OpenRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > AvailableLength)
            throw new ArgumentOutOfRangeException(nameof(length), "The range runs past the end of the selection.");

        if (length == 0)
            return new MemoryByteSource([]);

        long fileStart = FileOffsetOf(offset);
        long fileEnd = FileOffsetOf(offset + length - 1) + 1;
        return Window(Whole.OpenRange(fileStart, fileEnd - fileStart), offset, offset + length, fileStart);
    }

    public KeptIndexes KeptIndexes { get; } = new();

    /// <summary>Always: a selection is stretches of a file laid end to end, which is nothing anyone
    /// would write back anywhere.</summary>
    public bool IsReadOnly => true;

    public void Dispose()
    {
        KeptIndexes.Clear();
        Whole.Dispose();
    }

    private LineNumbering BuildNumbering()
    {
        var runs = Selection.Runs;
        var table = new LineRun[runs.Length];
        long viewLine = 1;
        for (int i = 0; i < runs.Length; i++)
        {
            table[i] = new LineRun(viewLine, runs[i].FirstLine, this.viewStarts[i], runs[i].FileOffset);
            viewLine += runs[i].LineCount;
        }

        if (table.Length == 0)
            return LineNumbering.FromStart;

        return Selection.Unit == SelectionUnit.Lines
            ? LineNumbering.FromRuns(table)
            : LineNumbering.ByLineStarts(table, AvailableLength);
    }

    private SelectionByteSource Window(IByteSource file, long viewStart, long viewEnd, long fileBase)
        => new(file, Selection.RunArray, this.viewStarts, viewStart, viewEnd, fileBase);

    /// <summary>
    /// Where <paramref name="view"/>, a range of this document, is in the file: the same bytes
    /// there, or only where it starts when it runs across a seam - its bytes are not together in
    /// the file.
    /// </summary>
    public ByteRange FileRangeOf(ByteRange view)
    {
        if (Selection.IsEmpty)
            return ByteRange.At(0);

        long start = FileOffsetOf(Math.Clamp(view.Offset, 0, AvailableLength - 1));
        if (view.Length <= 0 || view.End > AvailableLength)
            return ByteRange.At(start);

        long end = FileOffsetOf(view.End - 1) + 1;
        return end - start == view.Length ? new ByteRange(start, view.Length) : ByteRange.At(start);
    }

    /// <summary>
    /// Where <paramref name="file"/>, a range of the file, is in this document: the same bytes
    /// here when they are all selected, otherwise where the selection picks up at or after its
    /// start - what keeps a reloaded selection on the line it was on, or the next one still in it.
    /// </summary>
    public ByteRange ViewRangeOf(ByteRange file)
    {
        var runs = Selection.Runs;
        int lo = 0, hi = runs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (runs[mid].FileOffset + runs[mid].Length <= file.Offset)
                lo = mid + 1;
            else
                hi = mid;
        }

        // lo is the first run ending after the start: the one holding it, or the next.
        if (lo == runs.Length)
            return ByteRange.At(Math.Max(0, AvailableLength - 1));

        var run = runs[lo];
        if (file.Offset < run.FileOffset)
            return ByteRange.At(this.viewStarts[lo]);

        long view = this.viewStarts[lo] + (file.Offset - run.FileOffset);
        return file.End <= run.FileOffset + run.Length ? new ByteRange(view, file.Length) : ByteRange.At(view);
    }

    /// <summary>Where view byte <paramref name="view"/> is in the file. A newline a run adds is
    /// not in the file; it maps to the run's end, so a span ending on one maps no further.</summary>
    private long FileOffsetOf(long view)
    {
        int i = SelectionByteSource.RunAt(this.viewStarts, view);
        var run = Selection.Runs[i];
        return run.FileOffset + Math.Min(view - this.viewStarts[i], run.Length - 1);
    }

    private static string Describe(LineSelection selection, Func<long, string> number)
        => selection.Unit == SelectionUnit.Lines
            ? $"{number(selection.LineCount)} {(selection.LineCount == 1 ? "line" : "lines")} selected"
            : $"{number(selection.Runs.Length)} {(selection.Runs.Length == 1 ? "range" : "ranges")} selected";

    private static string Exact(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
