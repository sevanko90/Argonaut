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
    /// disagree about what a row looks like.
    /// </summary>
    internal static char SubstituteControl(char c)
        => !IsDisplayControl(c) ? c
            : c == Delete ? DeletePicture
            : (char)(ControlPicturesBase + c);

    /// <summary>C0 controls and DEL are substituted; tab passes through (TextBlock renders it).</summary>
    internal static bool IsDisplayControl(char c) => (c < ' ' && c != '\t') || c == Delete;
}
