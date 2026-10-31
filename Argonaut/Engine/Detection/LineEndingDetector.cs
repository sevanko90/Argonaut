using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Detection;

/// <summary>
/// Tallies the line breaks in the first <c>sampleLimit</c> bytes of a document, the way an
/// editor infers a file's line endings: there is no marker in the file, only its bytes. Bounded
/// so a multi-GB document costs one short read, which also means a document that only starts
/// mixing its line endings past the sample is reported by what its start uses.
/// </summary>
public sealed class LineEndingDetector : ILineEndingDetector
{
    /// <summary>How much of a document is sampled unless a caller says otherwise.</summary>
    public const int DefaultSampleLimit = 1024 * 1024;

    private readonly int sampleLimit;

    public LineEndingDetector(int sampleLimit = DefaultSampleLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleLimit);
        this.sampleLimit = sampleLimit;
    }

    public Task<LineEndingTally> DetectAsync(IByteOrigin origin, CancellationToken stopping)
        => Task.Run(() => Tally(origin, stopping), stopping);

    /// <summary>Runs on a background thread: it may block in <see cref="IByteOrigin.WaitForLength"/>.</summary>
    private LineEndingTally Tally(IByteOrigin origin, CancellationToken stopping)
    {
        // Settled before length: a source that settles between the two reads must not hand back an
        // old length with a settled flag, which would look like the whole document.
        bool settled;
        long available;
        while (true)
        {
            settled = origin.LengthSettled;
            available = origin.AvailableLength;
            if (settled || available >= this.sampleLimit)
                break;

            origin.WaitForLength(this.sampleLimit, stopping);
        }

        stopping.ThrowIfCancellationRequested();

        long sampled = Math.Min(available, this.sampleLimit);
        bool wholeInput = settled && sampled == available;
        if (sampled == 0)
            return new LineEndingTally(0, 0, 0, 0, wholeInput);

        LineBreakCounts counts;
        var source = origin.OpenRange(0, sampled);
        try
        {
            counts = LineBreakCounts.Count(source, sampled, stopping);
        }
        finally
        {
            source.Release();
        }

        // A CR at the cut, with more of the document after it, may be the first half of a CRLF
        // whose LF was not sampled - it is not counted as either.
        long cr = counts.EndsInCr && !wholeInput ? counts.Cr - 1 : counts.Cr;
        return new LineEndingTally(counts.Lf - counts.CrLf, counts.CrLf, cr - counts.CrLf, sampled, wholeInput);
    }
}
