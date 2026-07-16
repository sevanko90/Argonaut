using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public readonly record struct NdJsonLineSpan(long Offset, int Length);

/// <summary>
/// A class that scans, calculates and holds line offset and length values
/// for a large memory-mapped file to allow fast seeking and loading of arbitrary lines
/// </summary>
public sealed class NdJsonOffsetIndex
{
    private const int BufferSize = 256 * 1024;
    private const int QueueCapacity = 4096;

    private readonly Lock sync = new();
    private readonly List<NdJsonLineSpan> lineSpans = new();
    private readonly BlockingCollection<NdJsonLineSpan> pendingLineSpans = new(new ConcurrentQueue<NdJsonLineSpan>(), QueueCapacity);
    private TaskCompletionSource<bool>? lineCountReady;
    private int lineCountReadyTarget;
    private bool complete;

    private NdJsonOffsetIndex()
    {
    }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public int LineCount
    {
        get
        {
            lock (sync)
                return lineSpans.Count;
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (sync)
                return complete;
        }
    }

    public NdJsonLineSpan GetLineSpan(int lineIndex)
    {
        lock (sync)
            return lineSpans[lineIndex];
    }

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
    public static NdJsonOffsetIndex StartIndexing(MMapFile file, IProgressReporter? progressReporter = null)
    {
        var index = new NdJsonOffsetIndex();
        index.IndexingTask = Task.WhenAll(
            Task.Run(() => index.ProduceOffsets(file, progressReporter)),
            Task.Run(index.ConsumeOffsets));
        return index;
    }

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
                    pendingLineSpans.Add(new NdJsonLineSpan(currentLineStart, lineLength));
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
                pendingLineSpans.Add(new NdJsonLineSpan(currentLineStart, checked((int)(length - currentLineStart))));
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
                if (lineCountReady is not null && !lineCountReady.Task.IsCompleted && lineCountReadyTarget > 0 && lineSpans.Count >= lineCountReadyTarget)
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
