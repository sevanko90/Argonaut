using System.Threading;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Detection;

/// <summary>
/// The raw counts of <c>\n</c>, <c>\r</c> and <c>\r\n</c> in the start of a source, and whether
/// the counted range ends on a <c>\r</c> - the one count both detecting a document's line endings
/// and converting them are built on.
/// </summary>
internal readonly record struct LineBreakCounts(long Lf, long Cr, long CrLf, bool EndsInCr)
{
    /// <summary>
    /// Counts the first <paramref name="length"/> bytes of <paramref name="source"/> with SIMD
    /// counts per contiguous run, carrying a <c>\r</c> at the end of one run to a <c>\n</c> that
    /// starts the next. Advances by what each read returns and stops early if the source does.
    /// A background caller's: over a whole document this is a full read.
    /// </summary>
    public static LineBreakCounts Count(IByteSource source, long length, CancellationToken stopping)
    {
        long lf = 0, cr = 0, crLf = 0;
        bool previousEndedInCr = false;
        long offset = 0;
        while (offset < length)
        {
            var chunk = source.GetContiguousSpan(offset, (int)System.Math.Min(length - offset, ByteSourceReading.WriteChunkBytes));
            if (chunk.IsEmpty)
                break;

            lf += System.MemoryExtensions.Count(chunk, (byte)'\n');
            cr += System.MemoryExtensions.Count(chunk, (byte)'\r');
            crLf += System.MemoryExtensions.Count(chunk, "\r\n"u8);
            if (previousEndedInCr && chunk[0] == (byte)'\n')
                crLf++;

            previousEndedInCr = chunk[^1] == (byte)'\r';
            offset += chunk.Length;
            stopping.ThrowIfCancellationRequested();
        }

        return new LineBreakCounts(lf, cr, crLf, previousEndedInCr);
    }
}
