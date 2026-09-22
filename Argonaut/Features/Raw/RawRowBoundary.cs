using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Where one display row ends. One implementation, because every caller must agree to the byte:
/// the background scan, the on-demand rescan that re-derives rows between anchors, and the line
/// geometry <see cref="RawEditedRowIndex"/> computes rows from (<see cref="RawLineRows"/>). A
/// second implementation of these rules that drifted by one byte would put the caret on a
/// different row than the one being drawn.
///
/// <b>Forced breaks are cap-anchored.</b> The caps of a line sit at <c>lineStart + k·W</c>, and the
/// break at a cap is that cap backed off by 0..3 bytes so a multi-byte UTF-8 character is not
/// split. The next cap is <c>W</c> past this one <i>regardless of the backoff</i>, so every
/// boundary depends only on the few bytes at its own cap - never on where the previous row ended.
/// That is what makes a line's rows arithmetic from its start (<see cref="RawLineRows"/>), and what
/// lets an insert early in a long line leave every later boundary within 3 bytes of where it was
/// instead of chaining. Rows are therefore W-3..W+3 bytes (W+4 with a peek-extended newline) rather
/// than at most W, which is invisible: rows are already ragged in glyph count.
/// </summary>
internal static class RawRowBoundary
{
    /// <summary>
    /// A UTF-8 code point is at most 4 bytes, so at most 3 continuation bytes can precede a
    /// forced break before the break provably isn't splitting a valid character.
    /// </summary>
    internal const int MaxUtf8Backoff = 3;

    /// <summary>
    /// From a row start with its next cap at <paramref name="cap"/>, the row ends at: the first
    /// '\n' before the cap (kept inside the row, like the NDJSON index); end-of-data when it comes
    /// at or before the cap; a '\n' sitting exactly at the cap (peek-extended in as a real end, so
    /// a CRLF straddling the cap can't leave a lone linefeed row); otherwise a forced break at the
    /// cap, backed off by <see cref="BackoffAt"/>.
    ///
    /// Callers walk rows with <see cref="RawRowCursor"/>, which carries the cap from row to row.
    /// </summary>
    internal static (long End, bool SoftWrap, long NextCap) Next(IByteSource source, int wrapWidth, long start, long cap)
    {
        long length = source.AvailableLength;
        int searchLength = (int)Math.Min(cap - start, length - start);
        if (IndexOfNewline(source, start, searchLength) is long newlineOffset)
            return (newlineOffset + 1, false, newlineOffset + 1 + wrapWidth);

        if (cap >= length)
            return (length, false, length + wrapWidth); // end of data at or before the cap: a real end, no ⏎ marker

        if (ByteAt(source, cap) == (byte)'\n')
            return (cap + 1, false, cap + 1 + wrapWidth);

        return (cap - BackoffAt(source, cap), true, cap + wrapWidth);
    }

    /// <summary>
    /// How far a forced break at <paramref name="cap"/> backs off: the number of UTF-8
    /// continuation bytes ending at the cap, 0..3 - and 0 when all four bytes <c>cap-3..cap</c> are
    /// continuation bytes, because that is not valid UTF-8 and binary junk just breaks at the cap.
    ///
    /// Reads only the four bytes at the cap. The row before the cap always starts at least
    /// <c>W ≥ 4</c> bytes earlier, so the backoff can never empty it.
    /// </summary>
    internal static int BackoffAt(IByteSource source, long cap)
    {
        for (int back = 0; back < MaxUtf8Backoff; back++)
        {
            if (!IsUtf8ContinuationByte(ByteAt(source, cap - back)))
                return back;
        }

        return IsUtf8ContinuationByte(ByteAt(source, cap - MaxUtf8Backoff)) ? 0 : MaxUtf8Backoff;
    }

    /// <summary>
    /// Absolute offset of the first '\n' in [start, start + searchLength), or null. Loops because
    /// <see cref="IByteSource.GetContiguousSpan"/> truncates at an internal boundary: over a plain
    /// mapping that is one iteration and the same SIMD-vectorized span IndexOf as before, and over
    /// a piece table it is one iteration per piece the range straddles.
    /// </summary>
    internal static long? IndexOfNewline(IByteSource source, long start, long searchLength)
    {
        long scan = start;
        long remaining = searchLength;
        while (remaining > 0)
        {
            var span = source.GetContiguousSpan(scan, (int)Math.Min(remaining, int.MaxValue));
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

    /// <summary>Single byte at <paramref name="offset"/>; 0 at or past the end.</summary>
    internal static byte ByteAt(IByteSource source, long offset)
    {
        var span = source.GetContiguousSpan(offset, 1);
        return span.IsEmpty ? (byte)0 : span[0];
    }

    private static bool IsUtf8ContinuationByte(byte b) => (b & 0xC0) == 0x80;
}
