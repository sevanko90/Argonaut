using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.NdJson;

/// <summary>
/// Record used to hold data for the index of a large memory-mapped file to allow fast seeking and loading of arbitrary lines
/// </summary>
/// <param name="Offset">Byte offset into the file</param>
/// <param name="Length">Number of bytes to index</param>
public readonly record struct FileLineSpan(long Offset, int Length);

/// <summary>
/// A class that scans, calculates, and holds line offset and length values
/// for a large memory-mapped file to allow fast seeking and loading of arbitrary lines
/// </summary>
public sealed class FileOffsetIndex : AppendLogIndexBase<FileLineSpan>, IFileIndexer
{
    // Size of the window scanned per outer-loop pass. Scanning is zero-copy (spans over the
    // mapped file), so this only bounds progress-reporting granularity and span length —
    // nothing is allocated per window.
    private const int ScanChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Hidden constructor - use <see cref="FileOffsetIndex.StartIndexing"/>
    /// </summary>
    private FileOffsetIndex()
    {
    }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public string ItemNoun => "lines";

    /// <summary>
    /// Returns the number of lines in the index (may be less than the actual number of lines until <see cref="AppendLogIndexBase{T}.IsComplete"/> is true).
    /// </summary>
    public int LineCount => this.ItemCount;

    /// <summary>
    /// Return the index for a specified line number
    /// </summary>
    /// <param name="lineIndex">Line number for which to return the index data</param>
    /// <returns>Index data for the specified line number</returns>
    public FileLineSpan GetLineSpan(int lineIndex)
    {
        return this.items.ItemRef(lineIndex);
    }

    /// <summary>
    /// Waits (asynchronously) for the indexer to reach a target line count
    /// </summary>
    /// <param name="targetCount">number of lines that must be indexed before the task completes</param>
    /// <returns>A task that completes once the index is complete or contains the target number of lines</returns>
    public Task WaitForLineCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

    /// <summary>
    /// Start the process of indexing the file and returns a container object containing the background indexer
    /// </summary>
    /// <param name="file">Memory mapped file to index</param>
    /// <param name="progressReporter">Progress reporter</param>
    /// <returns>The index class, initially running in the background</returns>
    public static FileOffsetIndex StartIndexing(MMapFile file, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        var index = new FileOffsetIndex();
        index.IndexingTask = Task.Run(() => index.ProduceOffsets(file, progressReporter, cancellationToken), cancellationToken);
        return index;
    }

    /// <summary>
    /// Read the file and find byte offset and length of each line, appending directly into
    /// the base's append log as they're found.
    ///
    /// This used to hand each span to a second task via a bounded BlockingCollection, on the
    /// assumption that consuming while producing would help. It doesn't: the log is what
    /// WaitForLineCountAsync/GetLineSpan actually observe, so routing through a queue first
    /// added no visibility earlier than appending directly does, and benchmarking showed the
    /// bounded queue's blocking/signaling overhead made indexing 15% (long lines) to ~7x (short
    /// lines) slower than just appending from this one thread.
    /// </summary>
    /// <param name="file">Memory-mapped file to index</param>
    /// <param name="progressReporter">Allows callers to be notified of progress</param>
    /// <param name="cancellationToken">
    /// Checked once per scan chunk so a caller tearing down the owning <see cref="MMapFile"/>
    /// (e.g. window close mid-scan) can stop this loop before it dereferences memory the OS
    /// has unmapped - see CLAUDE.md / MMapFile for why touching the mapping after disposal is
    /// a native use-after-free, not a catchable .NET exception.
    /// </param>
    /// <remarks>Invoked in the background via a task</remarks>
    private void ProduceOffsets(MMapFile file, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        long length = file.Length;
        if (length == 0)
        {
            this.MarkComplete();
            progressReporter?.Report("Indexing");
            return;
        }

        long offset = 0;
        long currentLineStart = 0;
        try
        {
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Scan the mapped bytes directly - no buffer, no copy. IndexOf over a byte
                // span is SIMD-vectorized, which is what makes this loop fast on multi-GB files.
                int size = (int)Math.Min(ScanChunkSize, length - offset);
                var chunk = file.GetSpan(offset, size);

                int pos = 0;
                while (pos < size)
                {
                    int newlineIndex = chunk.Slice(pos).IndexOf((byte)'\n');
                    if (newlineIndex < 0)
                        break;

                    long lineEndExclusive = offset + pos + newlineIndex + 1;
                    int lineLength = checked((int)(lineEndExclusive - currentLineStart));
                    this.AddLineSpan(new FileLineSpan(currentLineStart, lineLength));
                    currentLineStart = lineEndExclusive;
                    pos += newlineIndex + 1;
                }

                offset += size;
                progressReporter?.Report("Indexing", offset, length);
            }
        }
        finally
        {
            if (currentLineStart < length)
            {
                this.AddLineSpan(new FileLineSpan(currentLineStart, checked((int)(length - currentLineStart))));
            }

            this.MarkComplete();
            progressReporter?.Report("Indexing", length, length);
        }
    }

    private void AddLineSpan(FileLineSpan lineSpan)
    {
        int newCount = this.items.Add(lineSpan) + 1;
        this.OnItemsPublished(newCount);
    }
}
