using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

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
    private const int BufferSize = 256 * 1024;
    private const int QueueCapacity = 4096;

    private readonly Lock sync = new();
    private readonly List<FileLineSpan> lineSpans = new();

    private readonly BlockingCollection<FileLineSpan> pendingLineSpans =
        new(new ConcurrentQueue<FileLineSpan>(), QueueCapacity);

    private TaskCompletionSource<bool>? lineCountReady;
    private int lineCountReadyTarget;
    private bool complete;
    
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
    public int LineCount
    {
        get
        {
            lock (sync)
                return lineSpans.Count;
        }
    }

    /// <summary>
    /// True if the file has been fully indexed.
    /// </summary>
    public bool IsComplete
    {
        get
        {
            lock (sync)
                return complete;
        }
    }

    /// <summary>
    /// Return the index for a specified line number
    /// </summary>
    /// <param name="lineIndex">Line number for which to return the index data</param>
    /// <returns>Index data for the specified line number</returns>
    public FileLineSpan GetLineSpan(int lineIndex)
    {
        lock (sync)
            return lineSpans[lineIndex];
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
        index.IndexingTask = Task.WhenAll(
            Task.Run(() => index.ProduceOffsets(file, progressReporter)),
            Task.Run(index.ConsumeOffsets));
        return index;
    }

    /// <summary>
    /// Read the file and find byte offset and length of each line, populating the internal index.
    /// </summary>
    /// <param name="file">Memory-mapped file to index</param>
    /// <param name="progressReporter">Allows callers to be notified of progress</param>
    /// <remarks>Invoked in the background via a task</remarks>
    private void ProduceOffsets(MMapFile file, IProgressReporter? progressReporter)
    {
        long length = file.Length;
        if (length == 0)
        {
            pendingLineSpans.CompleteAdding();
            MarkComplete();
            progressReporter?.Report(0, 0);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long offset = 0;
        long currentLineStart = 0;
        try
        {
            while (offset < length)
            {
                int bytesRead = file.Read(offset, buffer);
                if (bytesRead == 0)
                    break;

                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] != (byte)'\n')
                        continue;

                    long lineEndExclusive = offset + i + 1;
                    int lineLength = checked((int)(lineEndExclusive - currentLineStart));
                    pendingLineSpans.Add(new FileLineSpan(currentLineStart, lineLength));
                    currentLineStart = lineEndExclusive;
                }

                offset += bytesRead;
                var percent = (int)Math.Min(100, (offset * 100L) / length);
                if (percent % 5 == 0)
                {
                    progressReporter?.Report(offset, length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (currentLineStart < length)
            {
                pendingLineSpans.Add(new FileLineSpan(currentLineStart, checked((int)(length - currentLineStart))));
            }

            pendingLineSpans.CompleteAdding();
            progressReporter?.Report(length, length);
        }
    }

    private void ConsumeOffsets()
    {
        foreach (var lineSpan in pendingLineSpans.GetConsumingEnumerable())
        {
            TaskCompletionSource<bool>? waiter = null;
            lock (sync)
            {
                lineSpans.Add(lineSpan);
                if (lineCountReady is not null && !lineCountReady.Task.IsCompleted && lineCountReadyTarget > 0 &&
                    lineSpans.Count >= lineCountReadyTarget)
                    waiter = lineCountReady;
            }

            waiter?.TrySetResult(true);
        }

        MarkComplete();
        lineCountReady?.TrySetResult(true);
    }

    private void MarkComplete()
    {
        lock (sync)
            complete = true;
    }
}