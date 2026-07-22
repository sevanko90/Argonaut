using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Decodes one indexed display row to text straight from the mapped file bytes. The raw
/// viewer makes no assumptions about content, so decoding must never choke: invalid UTF-8
/// becomes U+FFFD (the default decoder fallback), and control characters are mapped to their
/// Unicode Control Pictures so a binary row renders as visible glyphs rather than upsetting
/// text layout. Every byte yields at most one char, so a row's display text is never longer
/// than its (cap-bounded) byte length.
/// </summary>
public static class RawRowReader
{
    public static string ReadRow(MMapFile file, long start, long endExclusive, bool isSoftWrapped)
    {
        int length = (int)(endExclusive - start);

        // Only a real line end can carry newline bytes (a soft-wrapped segment never ends in
        // '\n' - the indexer's peek rule pulls one at the cap into the segment as a real end).
        if (!isSoftWrapped)
        {
            var span = file.GetSpan(start, length);
            while (length > 0 && span[length - 1] is (byte)'\n' or (byte)'\r')
                length--;
        }

        return Sanitize(file.GetUtf8String(start, length));
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
            {
                char c = src[i];
                dest[i] = !IsDisplayControl(c)
                    ? c
                    : c == '\u007F' ? '\u2421'      // DEL -> Control Picture for delete
                    : (char)(0x2400 + c);           // C0  -> U+2400..U+241F Control Pictures
            }
        });
    }

    /// <summary>C0 controls and DEL are substituted; tab passes through (TextBlock renders it).</summary>
    private static bool IsDisplayControl(char c) => (c < ' ' && c != '\t') || c == '\u007F';
}
