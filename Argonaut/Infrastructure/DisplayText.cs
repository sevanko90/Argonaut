using System.Text;

namespace Argonaut.Infrastructure;

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
    public static string Read(MMapFile file, long offset, int length, out bool truncated, int maxLength = MaxLength)
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
        var span = file.GetSpan(offset, maxLength + 1);
        int cut = maxLength;
        while (cut > 0 && (span[cut] & 0xC0) == 0x80)
            cut--;

        return Encoding.UTF8.GetString(span[..cut]) + "…";
    }
}
