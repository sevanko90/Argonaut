using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

public sealed class NdJsonOffsetIndex
{
    private const int BufferSize = 256 * 1024;
    private const int QueueCapacity = 4096;

    private readonly object sync = new();
    private readonly List<long> lineStarts = new();
    private readonly BlockingCollection<long> pendingLineStarts = new(new ConcurrentQueue<long>(), QueueCapacity);
    private TaskCompletionSource<bool>? lineCountReady;
    private int lineCountReadyTarget;
    private bool complete;

    private NdJsonOffsetIndex()
    {
        lineStarts.Add(0);
    }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public int LineCount
    {
        get
        {
            lock (sync)
                return lineStarts.Count;
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

    public long GetOffset(int lineIndex)
    {
        lock (sync)
            return lineStarts[lineIndex];
    }

    public long GetLength(int lineIndex, long fileLength)
    {
        lock (sync)
        {
            var start = lineStarts[lineIndex];
            var end = lineIndex + 1 < lineStarts.Count ? lineStarts[lineIndex + 1] : fileLength;
            return end - start;
        }
    }

    public Task WaitForLineCountAsync(int targetCount)
    {
        lock (sync)
        {
            if (lineStarts.Count >= targetCount || complete)
                return Task.CompletedTask;

            if (lineCountReady is null || lineCountReady.Task.IsCompleted || targetCount > lineCountReadyTarget)
            {
                lineCountReadyTarget = targetCount;
                lineCountReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return lineCountReady.Task;
        }
    }

    public static NdJsonOffsetIndex Start(MMapFile file, IProgressReporter? progressReporter = null)
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
            pendingLineStarts.CompleteAdding();
            MarkComplete();
            progressReporter?.Report(0, 0);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long offset = 0;
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

                    long nextLineStart = offset + i + 1;
                    if (nextLineStart < length)
                        pendingLineStarts.Add(nextLineStart);
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
            pendingLineStarts.CompleteAdding();
            progressReporter?.Report(length, length);
        }
    }

    private void ConsumeOffsets()
    {
        foreach (var lineStart in pendingLineStarts.GetConsumingEnumerable())
        {
            TaskCompletionSource<bool>? waiter = null;
            lock (sync)
            {
                lineStarts.Add(lineStart);
                if (lineCountReady is not null && !lineCountReady.Task.IsCompleted && lineCountReadyTarget > 0 && lineStarts.Count >= lineCountReadyTarget)
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
