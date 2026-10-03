using System.Globalization;
using System.Text;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Text;

/// <summary>
/// Decodes mapped file bytes to text bounded by a display cap.
///
/// Every view in this app maps rows onto file ranges the file itself controls the size of, so
/// "one row" is only as small as the data happens to be. A minified JSON document, for example,
/// is a single newline-free line - to the line-oriented views that is one row tens or hundreds
/// of megabytes wide. Handing that to a TextBlock stalls the UI thread outright: Avalonia lays
/// out an unwrapped line in O(length), and the string itself is a large-object-heap allocation
/// on a path that runs per realized row.
///
/// So no display path decodes a whole range - it decodes at most <paramref name="maxLength"/>
/// bytes and marks the result with an ellipsis. Only paths whose correctness needs the real
/// bytes (parsing a selected NDJSON line into its JSON tree, scanning for a search term) read
/// spans in full, and those don't build strings per row.
/// </summary>
public static class DisplayText
{
    /// <summary>
    /// Default cap for any single decoded display string - a scalar JSON value, a property
    /// name, an NDJSON line, a CSV cell. Far wider than any viewport, so capping is invisible
    /// on real data and only bites on the pathological rows described above.
    /// </summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// Decodes [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>)
    /// as UTF-8, truncated to <paramref name="maxLength"/> bytes plus a trailing ellipsis.
    /// </summary>
    /// <param name="truncated">True when the range was longer than the cap.</param>
    public static string Read(IByteSource file, long offset, int length, out bool truncated, int maxLength = MaxLength)
    {
        if (length <= 0)
        {
            truncated = false;
            return string.Empty;
        }

        if (length <= maxLength)
        {
            truncated = false;
            return file.GetUtf8String(offset, length);
        }

        truncated = true;

        // Cut on a UTF-8 character boundary: read one byte past the cap and back the cut off
        // while the first excluded byte is a continuation byte (0b10xxxxxx), so a multi-byte
        // character is never split into a replacement glyph.
        var span = file.RequireContiguous(offset, maxLength + 1);
        int cut = maxLength;
        while (cut > 0 && (span[cut] & 0xC0) == 0x80)
            cut--;

        return Encoding.UTF8.GetString(span[..cut]) + "…";
    }

    /// <summary>
    /// Inserts a newline into every run of more than <paramref name="maxRun"/> characters that
    /// holds no whitespace, so wrapped text never asks the layout engine to break a run it has no
    /// break opportunity in. Avalonia's emergency wrap of such a run costs quadratic time in its
    /// length - 256K characters of base64 or a hash takes seconds per layout pass - while the
    /// same text cut into short paragraphs lays out in linear time.
    ///
    /// The result is for display only; it is no longer the value. Returns
    /// <paramref name="text"/> itself when no run needs breaking.
    /// </summary>
    public static string BreakLongRuns(string text, int maxRun)
    {
        StringBuilder? broken = null;
        int copiedTo = 0;
        int run = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                run = 0;
                continue;
            }

            // A break waits for a position that does not split a character: the second half of a
            // surrogate pair, a combining mark or a joiner belongs with what precedes it.
            if (run >= maxRun && StartsCharacter(text, i))
            {
                broken ??= new StringBuilder(text.Length + text.Length / maxRun + 1);
                broken.Append(text, copiedTo, i - copiedTo).Append('\n');
                copiedTo = i;
                run = 0;
            }

            run++;
        }

        return broken is null ? text : broken.Append(text, copiedTo, text.Length - copiedTo).ToString();
    }

    private static bool StartsCharacter(string text, int index)
    {
        char c = text[index];
        if (char.IsLowSurrogate(c) || c == '\u200D' || text[index - 1] == '\u200D' || c is >= '\uFE00' and <= '\uFE0F')
            return false;

        return CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);
    }
}
