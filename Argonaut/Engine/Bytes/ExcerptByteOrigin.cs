using System;
using System.Globalization;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Text;

namespace Argonaut.Engine.Bytes;

/// <summary>
/// Part of another document, opened as a document in its own right - what <c>--lines</c> and
/// <c>--bytes</c> open. Every view, search and view switch sees only the excerpt: a mapping of
/// only its bytes, an index of only its lines, a scrollbar its size. That is the point of it over
/// opening the whole document and jumping: indexing time and memory scale with the excerpt, not
/// the file.
///
/// It is <see cref="IByteOrigin.OpenRange"/> on <see cref="Whole"/>, offset - the same mechanism
/// that makes one NDJSON line or one JSON array a document - so it holds nothing of its own beyond
/// where it sits.
///
/// An excerpt taken by lines knows which line of the file it starts at (<see cref="Lines"/>). One
/// taken by bytes does not: finding out would mean counting every newline before it, which is
/// the scan opening by bytes exists to skip.
///
/// <see cref="Path"/> is null, deliberately: the excerpt is not the file, so recent files and the
/// remembered schema binding pass it over, and a save is a Save As - an
/// edited excerpt can never be written over the whole file it came from.
///
/// Owns <see cref="Whole"/>, and disposes it.
/// </summary>
public sealed class ExcerptByteOrigin : IByteOrigin
{
    /// <summary>An excerpt of the lines <paramref name="lines"/> located.</summary>
    public ExcerptByteOrigin(IByteOrigin whole, LocatedLines lines)
        : this(whole, lines.Range, lines, Describe(lines, Exact), Describe(lines, CompactCount.FormatRange))
    {
    }

    private ExcerptByteOrigin(IByteOrigin whole, ByteRange range, LocatedLines? lines, string description, string shortDescription)
    {
        ArgumentNullException.ThrowIfNull(whole);
        if (range.Offset < 0 || range.Length < 0 || range.End > whole.AvailableLength)
            throw new ArgumentOutOfRangeException(nameof(range), "The excerpt must lie within what has arrived of the whole document.");

        Whole = whole;
        Range = range;
        Lines = lines;
        Description = description;
        ShortDescription = shortDescription;
        DisplayName = $"{whole.DisplayName} ({description})";
    }

    /// <summary>An excerpt of the bytes in <paramref name="range"/>, which it takes as given.</summary>
    public static ExcerptByteOrigin OfBytes(IByteOrigin whole, ByteRange range)
        => new(whole, range, lines: null, Describe(range, Exact), Describe(range, CompactCount.FormatRange));

    /// <summary>The document this is an excerpt of.</summary>
    public IByteOrigin Whole { get; }

    /// <summary>Where the excerpt sits in <see cref="Whole"/>.</summary>
    public ByteRange Range { get; }

    /// <summary>Which lines of <see cref="Whole"/> this is, or null for an excerpt taken by
    /// bytes - see the class remarks.</summary>
    public LocatedLines? Lines { get; }

    /// <summary>Which part of the file this is: "lines 1,000,000–2,000,000", "bytes 0–499".</summary>
    public string Description { get; }

    /// <summary><see cref="Description"/> with its numbers rounded to fit a label - "lines
    /// 1m–2m" (see <see cref="CompactCount"/>).</summary>
    public string ShortDescription { get; }

    public string DisplayName { get; }

    /// <summary>Always null - see the class remarks.</summary>
    public string? Path => null;

    /// <summary>The excerpt's length. Always settled: its bytes had arrived before it could be
    /// opened.</summary>
    public long AvailableLength => Range.Length;

    public IByteSource Open() => Whole.OpenRange(Range.Offset, Range.Length);

    public IByteSource OpenRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > Range.Length)
            throw new ArgumentOutOfRangeException(nameof(length), "The range runs past the end of the excerpt.");

        return Whole.OpenRange(Range.Offset + offset, length);
    }

    public KeptIndexes KeptIndexes { get; } = new();

    /// <summary>"lines 1,000,000–2,000,000", or "lines 1,000,000–end" for one that runs to the
    /// end of the document, with the two ends written together by <paramref name="numbers"/> -
    /// so a rounding one can keep them from reading the same.</summary>
    public static string Describe(LocatedLines lines, Func<long, long, (string First, string Last)> numbers)
    {
        if (lines.LastLine is not { } last)
            return $"lines {numbers(lines.FirstLine, lines.FirstLine).First}–end";

        var (a, b) = numbers(lines.FirstLine, last);
        return last == lines.FirstLine ? $"line {a}" : $"lines {a}–{b}";
    }

    /// <summary>"bytes 0–499": zero-based, last byte included, as an HTTP range names them.</summary>
    public static string Describe(ByteRange range, Func<long, long, (string First, string Last)> numbers)
    {
        var (a, b) = numbers(range.Offset, Math.Max(range.Offset, range.End - 1));
        return range.Length <= 1 ? $"byte {a}" : $"bytes {a}–{b}";
    }

    private static (string, string) Exact(long first, long last)
        => (first.ToString("N0", CultureInfo.CurrentCulture), last.ToString("N0", CultureInfo.CurrentCulture));

    public void Dispose()
    {
        KeptIndexes.Clear();
        Whole.Dispose();
    }
}
