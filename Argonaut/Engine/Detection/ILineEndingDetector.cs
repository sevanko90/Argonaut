using System.Threading;
using System.Threading.Tasks;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Detection;

/// <summary>
/// Counts the line breaks at the start of a document. The work runs off the calling thread, so a
/// caller on the UI thread awaits the result without blocking on the read.
/// </summary>
public interface ILineEndingDetector
{
    /// <summary>
    /// Tallies the line breaks in the start of <paramref name="origin"/>, waiting for a streamed
    /// document's bytes to arrive as far as the sample needs. Opens and releases its own source.
    /// </summary>
    Task<LineEndingTally> DetectAsync(IByteOrigin origin, CancellationToken stopping);
}
