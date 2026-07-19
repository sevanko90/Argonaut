using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// A background file indexer started via a StartIndexing(MMapFile, IProgressReporter?,
/// CancellationToken) factory: it scans the whole mapped file once and publishes fixed-size
/// records as it goes. Implemented by FileOffsetIndex (lines) and JsonStructureIndex
/// (tokens). Lets generic consumers - the completion monitor, IndexedFileSession - work
/// with either indexer without knowing which one they have.
///
/// FileSearchSession shares the same publishing machinery (AppendLogIndexBase) but is
/// intentionally NOT an IFileIndexer: its IsComplete means "the scan stopped", including
/// cancellation and the match cap, so treating it as a finished index would misreport.
/// </summary>
public interface IFileIndexer
{
    /// <summary>
    /// The background indexing task. Completes when the file is fully indexed; faults if
    /// indexing failed or was cancelled. This is what a completion monitor awaits.
    /// </summary>
    Task IndexingTask { get; }

    /// <summary>True once the file has been fully indexed (lock-free read).</summary>
    bool IsComplete { get; }

    /// <summary>Records published so far (may grow until <see cref="IsComplete"/> is true).</summary>
    int ItemCount { get; }

    /// <summary>Display noun for the indexed records ("lines", "tokens") for status text.</summary>
    string ItemNoun { get; }
}
