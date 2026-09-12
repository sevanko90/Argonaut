using System;
using System.Buffers;
using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Decodes one indexed display row to text straight from the underlying bytes. The raw viewer
/// makes no assumptions about content, so decoding must never choke: invalid UTF-8 becomes
/// U+FFFD (the default decoder fallback), and control characters are mapped to their Unicode
/// Control Pictures so a binary row renders as visible glyphs rather than upsetting text layout.
/// Every byte yields at most one char, so a row's display text is never longer than its
/// (cap-bounded) byte length - an upper bound, not a 1:1 mapping. A caller that needs to know
/// which byte a given char came from (the caret) wants <see cref="RawRowDecoder"/> instead.
/// </summary>
public static class RawRowReader
{
    private const char Delete = (char)0x7F;

    /// <summary>Unicode Control Picture for DEL (U+2421).</summary>
    private const char DeletePicture = (char)0x2421;

    /// <summary>Start of the Control Pictures block (U+2400), which runs parallel to C0.</summary>
    private const int ControlPicturesBase = 0x2400;

    /// <summary>U+0085 NEXT LINE, the C1 control - Unicode's form of EBCDIC's NL.</summary>
    private const char NextLine = (char)0x0085;

    /// <summary>U+2028 LINE SEPARATOR.</summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>U+2029 PARAGRAPH SEPARATOR.</summary>
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>U+2424 SYMBOL FOR NEWLINE, shown for <see cref="NextLine"/>.</summary>
    internal const char NextLinePicture = (char)0x2424;

    /// <summary>U+21B5, shown for <see cref="LineSeparator"/>.</summary>
    internal const char LineSeparatorPicture = (char)0x21B5;

    /// <summary>U+00B6 PILCROW, shown for <see cref="ParagraphSeparator"/>.</summary>
    internal const char ParagraphSeparatorPicture = (char)0x00B6;

    public static string ReadRow(IByteSource source, long start, long endExclusive, bool isSoftWrapped)
    {
        int length = (int)(endExclusive - start);
        if (length <= 0)
            return string.Empty;

        // Fast path: a row served whole (always, over a plain mapping) decodes with no copy.
        var contiguous = source.GetContiguousSpan(start, length);
        if (contiguous.Length == length)
            return Decode(contiguous, isSoftWrapped);

        // The row straddles a piece boundary, so gather it first. Rows are cap-bounded, so this
        // rents kilobytes at most.
        byte[] gathered = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int copied = source.CopyTo(start, gathered.AsSpan(0, length));
            return Decode(gathered.AsSpan(0, copied), isSoftWrapped);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(gathered);
        }
    }

    private static string Decode(ReadOnlySpan<byte> row, bool isSoftWrapped)
    {
        // Only a real line end can carry newline bytes (a soft-wrapped segment never ends in
        // '\n' - the indexer's peek rule pulls one at the cap into the segment as a real end).
        if (!isSoftWrapped)
        {
            while (row.Length > 0 && row[^1] is (byte)'\n' or (byte)'\r')
                row = row[..^1];
        }

        return Sanitize(Encoding.UTF8.GetString(row));
    }

    private static string Sanitize(string text)
    {
        // Fast path: clean text (the common case) is returned as-is, no second allocation.
        int firstControl = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsDisplayControl(text[i]))
            {
                firstControl = i;
                break;
            }
        }

        if (firstControl < 0)
            return text;

        return string.Create(text.Length, (text, firstControl), static (dest, state) =>
        {
            var (src, from) = state;
            src.AsSpan(0, from).CopyTo(dest);
            for (int i = from; i < src.Length; i++)
                dest[i] = SubstituteControl(src[i]);
        });
    }

    /// <summary>
    /// The display substitution, shared with <see cref="RawRowDecoder"/> so the two cannot
    /// disagree about what a row looks like. One char in, one char out - which is what lets
    /// <see cref="RawRowDecoder"/> substitute without touching its char-to-byte offset map.
    /// </summary>
    internal static char SubstituteControl(char c)
        => !IsDisplayControl(c) ? c
            : c == Delete ? DeletePicture
            : c == NextLine ? NextLinePicture
            : c == LineSeparator ? LineSeparatorPicture
            : c == ParagraphSeparator ? ParagraphSeparatorPicture
            : (char)(ControlPicturesBase + c);

    /// <summary>
    /// C0 controls, DEL, and the three separators that are not '\n' are substituted; tab passes
    /// through (the text layout renders it).
    ///
    /// NEL, LINE SEPARATOR and PARAGRAPH SEPARATOR are mandatory line breaks to Unicode (UAX #14)
    /// and so to the text layout, which would otherwise stack several lines of text inside one
    /// 22px row band and clip all but the first. They are NOT breaks to this viewer: rows are
    /// broken on '\n' bytes, because that is what every tool a user cross-references a line
    /// number with counts, and because the scan finds breaks with a one-byte vectorized IndexOf
    /// that a three-byte UTF-8 sequence cannot join. A lone CR is already handled the same way -
    /// shown inline as a glyph, not treated as an end of line.
    /// </summary>
    internal static bool IsDisplayControl(char c)
        => (c < ' ' && c != '\t')
            || c == Delete
            || c == NextLine
            || c == LineSeparator
            || c == ParagraphSeparator;

    /// <summary>
    /// True for the glyphs <see cref="SubstituteControl"/> produces. Shared with
    /// <see cref="RawWordStops"/>, which selects each of them alone: a substitution stands for a
    /// byte the file does not otherwise show, and selecting exactly one is what makes it a single
    /// thing to delete.
    ///
    /// A real U+2400-U+2426, U+21B5 or U+00B6 in the file decodes to the same char and is treated
    /// the same way. The cost is one glyph selecting alone, which is also what it looks like.
    /// </summary>
    internal static bool IsSubstitutionGlyph(char c)
        => c is >= (char)ControlPicturesBase and <= (char)0x2426
            || c == LineSeparatorPicture
            || c == ParagraphSeparatorPicture;
}
