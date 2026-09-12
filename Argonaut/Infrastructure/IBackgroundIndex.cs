using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// An index built in the background, started via a StartIndexing(IByteSource,
/// IProgressReporter?, CancellationToken) factory: it scans the whole source once and publishes
/// fixed-size records as it goes. Implemented by FileOffsetIndex (lines), JsonStructureIndex
/// (tokens) and RawSegmentIndex (display rows). Lets generic consumers - the completion monitor,
/// IndexedSourceSession - work with any of them without knowing which one they have.
///
/// SearchSession shares the same publishing machinery (AppendLogIndexBase) but is intentionally
/// NOT an IBackgroundIndex: it is not an index of the document, and stopping early at the match
/// cap is a normal outcome for it rather than a partial result.
/// </summary>
public interface IBackgroundIndex
{
    /// <summary>
    /// The background indexing task. Completes when the source is fully indexed; faults if
    /// indexing failed or was cancelled. This is what a completion monitor awaits.
    /// </summary>
    Task IndexingTask { get; }

    /// <summary>
    /// True once no further records will be published, whatever stopped the scan - it finished,
    /// it failed, or it was cancelled. Named for that rather than "complete" because it is set
    /// in a finally: a reader that treats it as "fully indexed" is wrong on the failure and
    /// cancellation paths, and must check <see cref="Failure"/> (or the scan's own flags) to
    /// tell those apart. What it does guarantee is the one thing every reader needs - that a
    /// count it has observed will not grow again (lock-free read).
    /// </summary>
    bool AllItemsPublished { get; }

    /// <summary>
    /// Non-null when the scan stopped because of an error; null on success *and* on
    /// cancellation. Set before <see cref="AllItemsPublished"/> becomes true.
    /// </summary>
    IndexFailure? Failure { get; }

    /// <summary>Records published so far (may grow until <see cref="AllItemsPublished"/> is true).</summary>
    int ItemCount { get; }
}
