using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Where one display row ends. Extracted from <see cref="RawSegmentIndex"/> because three
/// callers now need it and they must not be able to disagree: the background scan, the
/// on-demand rescan that re-derives rows between anchors, and the re-derivation of the edited
/// region in <see cref="RawEditedRowIndex"/>. A second implementation of these rules that drifted
/// by one byte would put the caret on a different row than the one being drawn.
/// </summary>
internal static class RawRowBoundary
{
    /// <summary>
    /// A UTF-8 code point is at most 4 bytes, so at most 3 continuation bytes can precede a
    /// forced break before the break provably isn't splitting a valid character.
    /// </summary>
    internal const int MaxUtf8Backoff = 3;

    /// <summary>
    /// From a row start, the row ends at: the first '\n' within the cap (kept inside the row,
    /// like the NDJSON index); end-of-data when it comes at or before the cap; a '\n' sitting
    /// exactly at the cap (peek-extended in as a real end, so a CRLF straddling the cap can't
    /// leave a lone linefeed row); otherwise a forced break at the cap, backed off up to 3 bytes
    /// so a multi-byte UTF-8 character isn't split (binary data just breaks at the cap).
    /// </summary>
    internal static (long End, bool SoftWrap) Next(IByteSource source, int wrapWidth, long start)
    {
        long length = source.AvailableLength;
        int searchLength = (int)Math.Min(wrapWidth, length - start);
        if (IndexOfNewline(source, start, searchLength) is long newlineOffset)
            return (newlineOffset + 1, false);

        if (start + wrapWidth >= length)
            return (length, false); // end of data at or before the cap: a real end, no ⏎ marker

        return BreakAtCap(source, wrapWidth, start);
    }

    /// <summary>
    /// Absolute offset of the first '\n' in [start, start + searchLength), or null. Loops because
    /// <see cref="IByteSource.GetContiguousSpan"/> truncates at an internal boundary: over a plain
    /// mapping that is one iteration and the same SIMD-vectorized span IndexOf as before, and over
    /// a piece table it is one iteration per piece the range straddles.
    /// </summary>
    private static long? IndexOfNewline(IByteSource source, long start, int searchLength)
    {
        long scan = start;
        int remaining = searchLength;
        while (remaining > 0)
        {
            var span = source.GetContiguousSpan(scan, remaining);
            if (span.IsEmpty)
                break; // end of data - only reachable if searchLength overran Length

            int hit = span.IndexOf((byte)'\n');
            if (hit >= 0)
                return scan + hit;

            scan += span.Length;
            remaining -= span.Length;
        }

        return null;
    }

    private static (long End, bool SoftWrap) BreakAtCap(IByteSource source, int wrapWidth, long segmentStart)
    {
        long capEnd = segmentStart + wrapWidth;
        if (ByteAt(source, capEnd) == (byte)'\n')
            return (capEnd + 1, false);

        long end = capEnd;
        for (int back = 0; back < MaxUtf8Backoff && end - 1 > segmentStart; back++)
        {
            if (!IsUtf8ContinuationByte(ByteAt(source, end)))
                return (end, true);

            end--;
        }

        return (IsUtf8ContinuationByte(ByteAt(source, end)) ? capEnd : end, true);
    }

    /// <summary>Single byte at <paramref name="offset"/>; 0 at or past the end.</summary>
    private static byte ByteAt(IByteSource source, long offset)
    {
        var span = source.GetContiguousSpan(offset, 1);
        return span.IsEmpty ? (byte)0 : span[0];
    }

    private static bool IsUtf8ContinuationByte(byte b) => (b & 0xC0) == 0x80;
}
