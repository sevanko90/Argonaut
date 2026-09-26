using System;
using Argonaut.Engine.Bytes;

namespace Argonaut.Features.Json.Indexing;

/// <summary>Turns "the parse failed after this token" into the offset a reader is sent to - the
/// failure banner's link and the incompatible-file placeholder both jump there.</summary>
internal static class JsonFailureLocation
{
    /// <summary>
    /// Where the document stops making sense, given where the last good token ended.
    ///
    /// The end of the last good token is not it: between there and the content that actually
    /// broke sits the punctuation joining the two - a comma before the next element, a colon
    /// before a value - and the newline and indentation after it. Reporting the token's end
    /// lands a reader at the tail of the previous line, one or two characters short of the thing
    /// they were sent to look at. So skip the joining syntax and stop on the first byte that is
    /// really content.
    ///
    /// Bounded rather than unbounded: a document padded with megabytes of whitespace should not
    /// turn error reporting into a scan, and stopping early only costs the precision this is
    /// trying to add.
    /// </summary>
    public static long StartOfTroubleAfter(IByteSource file, long lastGoodEnd)
    {
        long offset = lastGoodEnd;
        long limit = Math.Min(file.AvailableLength, offset + MaxTroubleSkip);
        while (offset < limit)
        {
            byte b = file.ByteAt(offset);
            bool joining = b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)':';
            if (!joining)
                break;

            offset++;
        }

        return StartOfLineContaining(file, offset);
    }

    /// <summary>
    /// Backs up to the first real character of the line <paramref name="offset"/> is on, so a
    /// reader is sent to the start of the offending line rather than partway along it - the last
    /// good token can be the opening brace of the very element that broke, which would otherwise
    /// land the caret just inside it.
    ///
    /// Budgeted, and that budget is doing real work rather than guarding a pathological case:
    /// minified JSON is a single line that can be the length of the whole file, and snapping to
    /// its start would send a reader to byte zero of a multi-GB document instead of to the
    /// problem. When no line break is found nearby, the precise offset is already the best answer
    /// available and is kept.
    /// </summary>
    private static long StartOfLineContaining(IByteSource file, long offset)
    {
        long floor = Math.Max(0, offset - MaxTroubleSkip);
        long lineStart = -1;
        for (long scan = offset - 1; scan >= floor; scan--)
        {
            if (file.ByteAt(scan) == (byte)'\n')
            {
                lineStart = scan + 1;
                break;
            }
        }

        if (lineStart < 0)
            return offset; // one very long line - keep the precise position

        while (lineStart < offset && file.ByteAt(lineStart) is (byte)' ' or (byte)'\t' or (byte)'\r')
            lineStart++;

        return lineStart;
    }

    /// <summary>Bytes of joining syntax and whitespace worth stepping over to find the trouble.</summary>
    private const int MaxTroubleSkip = 4096;
}
