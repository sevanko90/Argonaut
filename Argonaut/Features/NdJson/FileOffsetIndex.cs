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
public sealed class FileOffsetIndex
{
    // Size of the window scanned per outer-loop pass. Scanning is zero-copy (spans over the
    // mapped file), so this only bounds progress-reporting granularity and span length —
    // nothing is allocated per window.
    private const int ScanChunkSize = 4 * 1024 * 1024;

    // Single-writer (the indexing task) / multi-reader (UI). The log's volatile Count
    // publication is what lets LineCount/GetLineSpan run lock-free - see SegmentedAppendLog
    // for the full reasoning. FileLineSpan records are never mutated after publication, so
    // plain field reads of a published span are safe.
    private readonly SegmentedAppendLog<FileLineSpan> lineSpans = new();

    // Guards ONLY the cold waiter machinery below (registration and completion of
    // lineCountReady). Nothing on the per-line hot path takes this lock.
    private readonly Lock sync = new();

    private TaskCompletionSource<bool>? lineCountReady;
    private int lineCountReadyTarget;

    // Hot-path mirror of lineCountReadyTarget: 0 means "nobody is waiting", so the writer
    // can skip the waiter lock entirely with one volatile read per line. Written only
    // inside the sync lock.
    private volatile int pendingWaitTarget;

    // volatile: read lock-free by IsComplete/readers; written once by the writer thread.
    private volatile bool complete;

    /// <summary>
    /// Hidden constructor - use <see cref="FileOffsetIndex.StartIndexing"/>
    /// </summary>
    private FileOffsetIndex()
    {
    }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Returns the number of lines in the index (may be less than the actual number of lines until <see cref="IsComplete"/> is true).
    /// </summary>
    public int LineCount => lineSpans.Count;

    /// <summary>
    /// True if the file has been fully indexed.
    /// </summary>
    public bool IsComplete => complete;

    /// <summary>
    /// Return the index for a specified line number
    /// </summary>
    /// <param name="lineIndex">Line number for which to return the index data</param>
    /// <returns>Index data for the specified line number</returns>
    public FileLineSpan GetLineSpan(int lineIndex)
    {
        return lineSpans.ItemRef(lineIndex);
    }

    /// <summary>
    /// Waits (asynchronously) for the indexer to reach a target line count
    /// </summary>
    /// <param name="targetCount">number of lines that must be indexed before the task completes</param>
    /// <returns>A task that completes once the index is complete or contains the target number of lines</returns>
    public Task WaitForLineCountAsync(int targetCount)
    {
        lock (sync)
        {
            if (lineSpans.Count >= targetCount || complete)
                return Task.CompletedTask;

            if (lineCountReady is null || lineCountReady.Task.IsCompleted || targetCount > lineCountReadyTarget)
            {
                lineCountReadyTarget = targetCount;
                lineCountReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            pendingWaitTarget = lineCountReadyTarget;

            // Re-check after publishing the flag: the writer may have crossed the target
            // between the first check above and the flag becoming visible to it. The
            // condition is monotone (count only grows), so any later append also notices
            // the flag - this re-check only matters if no further line is ever appended.
            if (lineSpans.Count >= lineCountReadyTarget || complete)
            {
                var waiter = lineCountReady;
                pendingWaitTarget = 0;
                waiter.TrySetResult(true);
            }

            return lineCountReady.Task;
        }
    }

    /// <summary>
    /// Start the process of indexing the file and returns a container object containing the background indexer
    /// </summary>
    /// <param name="file">Memory mapped file to index</param>
    /// <param name="progressReporter">Progress reporter</param>
    /// <returns>The index class, initially running in the background</returns>
    public static FileOffsetIndex StartIndexing(MMapFile file, IProgressReporter? progressReporter = null)
    {
        var index = new FileOffsetIndex();
        index.IndexingTask = Task.Run(() => index.ProduceOffsets(file, progressReporter));
        return index;
    }

    /// <summary>
    /// Read the file and find byte offset and length of each line, appending directly into
    /// <see cref="lineSpans"/> as they're found.
    ///
    /// This used to hand each span to a second task via a bounded BlockingCollection, on the
    /// assumption that consuming while producing would help. It doesn't: lineSpans is what
    /// WaitForLineCountAsync/GetLineSpan actually observe, so routing through a queue first
    /// added no visibility earlier than appending directly does, and benchmarking showed the
    /// bounded queue's blocking/signaling overhead made indexing 15% (long lines) to ~7x (short
    /// lines) slower than just appending from this one thread.
    /// </summary>
    /// <param name="file">Memory-mapped file to index</param>
    /// <param name="progressReporter">Allows callers to be notified of progress</param>
    /// <remarks>Invoked in the background via a task</remarks>
    private void ProduceOffsets(MMapFile file, IProgressReporter? progressReporter)
    {
        long length = file.Length;
        if (length == 0)
        {
            MarkComplete();
            progressReporter?.Report("Indexing");
            return;
        }

        long offset = 0;
        long currentLineStart = 0;
        try
        {
            while (offset < length)
            {
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
                    AddLineSpan(new FileLineSpan(currentLineStart, lineLength));
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
                AddLineSpan(new FileLineSpan(currentLineStart, checked((int)(length - currentLineStart))));
            }

            MarkComplete();
            progressReporter?.Report("Indexing", length, length);
        }
    }

    private void AddLineSpan(FileLineSpan lineSpan)
    {
        int newCount = lineSpans.Add(lineSpan) + 1;

        // One volatile read per line instead of a lock (which would cost 10-20ns per line -
        // a large fraction of the vectorized scan's per-line budget). A transiently missed
        // flag is harmless: the condition is monotone, so the next append re-checks it, and
        // MarkComplete signals unconditionally if this was the last line.
        int waitTarget = pendingWaitTarget;
        if (waitTarget != 0 && newCount >= waitTarget)
            NotifyLineCountReady();
    }

    private void NotifyLineCountReady()
    {
        TaskCompletionSource<bool>? waiter = null;
        lock (sync)
        {
            if (lineCountReady is not null && !lineCountReady.Task.IsCompleted && lineCountReadyTarget > 0 &&
                lineSpans.Count >= lineCountReadyTarget)
            {
                waiter = lineCountReady;
                pendingWaitTarget = 0;
            }
        }

        waiter?.TrySetResult(true);
    }

    private void MarkComplete()
    {
        complete = true;

        TaskCompletionSource<bool>? waiter;
        lock (sync)
        {
            waiter = lineCountReady;
            pendingWaitTarget = 0;
        }

        waiter?.TrySetResult(true);
    }
}
