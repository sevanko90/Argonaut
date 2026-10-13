using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Text;

/// <summary>
/// Where UTF-8 characters start, for a cut made at an arbitrary byte - a wrap break, an excerpt
/// opened by byte offset. A character is at most four bytes, so finding its start never looks
/// back more than three.
/// </summary>
public static class Utf8Boundary
{
    /// <summary>The longest a backoff to a character start can be.</summary>
    public const int MaxBackoff = 3;

    /// <summary>True for a byte inside a multi-byte character rather than at its start
    /// (0b10xxxxxx).</summary>
    public static bool IsContinuationByte(byte b) => (b & 0xC0) == 0x80;

    /// <summary>
    /// <paramref name="offset"/>, moved back to the start of the character it falls inside -
    /// by at most <see cref="MaxBackoff"/> bytes, so bytes that are not UTF-8 at all (a run of
    /// continuation bytes in binary) cannot pull it back further.
    /// </summary>
    public static long CharacterStartAtOrBefore(IByteSource source, long offset)
    {
        long start = offset;
        while (start > 0 && offset - start < MaxBackoff && start < source.AvailableLength
               && IsContinuationByte(source.ByteAt(start)))
            start--;

        return start;
    }
}
