using System;
using System.Globalization;
using Argonaut.Engine.Indexing.Lines;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// A run of lines of another document, opened as a document in its own right - what
/// <c>--lines</c> opens. Every view, search and view switch sees only the excerpt: a mapping of
/// only its bytes, an index of only its lines, a scrollbar its size. That is the point of it over
/// opening the whole document and jumping: indexing time and memory scale with the excerpt, not
/// the file.
///
/// It is <see cref="IByteOrigin.OpenRange"/> on <see cref="Whole"/>, offset - the same mechanism
/// that makes one NDJSON line or one JSON array a document - so it holds nothing of its own beyond
/// where it sits.
///
/// <see cref="Path"/> is null, deliberately: the excerpt is not the file, so recent files, the
/// schema sidecar and the remembered schema binding pass it over, and a save is a Save As - an
/// edited excerpt can never be written over the whole file it came from.
///
/// Owns <see cref="Whole"/>, and disposes it.
/// </summary>
public sealed class ExcerptByteOrigin : IByteOrigin
{
    public ExcerptByteOrigin(IByteOrigin whole, LocatedLines lines)
    {
        ArgumentNullException.ThrowIfNull(whole);
        if (lines.Range.Offset < 0 || lines.Range.End > whole.AvailableLength)
            throw new ArgumentOutOfRangeException(nameof(lines), "The excerpt must lie within what has arrived of the whole document.");

        Whole = whole;
        Lines = lines;
        DisplayName = $"{whole.DisplayName} ({Describe(lines)})";
    }

    /// <summary>The document this is an excerpt of.</summary>
    public IByteOrigin Whole { get; }

    /// <summary>Which lines of <see cref="Whole"/> this is, and where they sit in it.</summary>
    public LocatedLines Lines { get; }

    public string DisplayName { get; }

    /// <summary>Always null - see the class remarks.</summary>
    public string? Path => null;

    /// <summary>The excerpt's length. Always settled: its bytes had arrived before it could be
    /// opened.</summary>
    public long AvailableLength => Lines.Range.Length;

    public IByteSource Open() => Whole.OpenRange(Lines.Range.Offset, Lines.Range.Length);

    public IByteSource OpenRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > Lines.Range.Length)
            throw new ArgumentOutOfRangeException(nameof(length), "The range runs past the end of the excerpt.");

        return Whole.OpenRange(Lines.Range.Offset + offset, length);
    }

    public KeptIndexes KeptIndexes { get; } = new();

    /// <summary>"lines 1,000,000–2,000,000", or "lines 1,000,000–end" for one that runs to the
    /// end of the document.</summary>
    public static string Describe(LocatedLines lines)
    {
        string first = lines.FirstLine.ToString("N0", CultureInfo.CurrentCulture);
        string last = lines.LastLine is { } l ? l.ToString("N0", CultureInfo.CurrentCulture) : "end";
        return lines.LastLine == lines.FirstLine ? $"line {first}" : $"lines {first}–{last}";
    }

    public void Dispose()
    {
        KeptIndexes.Clear();
        Whole.Dispose();
    }
}
