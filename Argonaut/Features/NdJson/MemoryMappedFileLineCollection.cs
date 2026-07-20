using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Threading;
using Argonaut.Infrastructure;

namespace Argonaut.Features.NdJson;

/// <summary>
/// Model that represents a line of a text file
/// </summary>
public sealed class MemoryMappedFileVisibleLine
{
    public MemoryMappedFileVisibleLine(int lineNumber, string text)
    {
        LineNumber = lineNumber;
        Text = text;
    }

    public int LineNumber { get; }

    public string Text { get; }
}

// Backs the ListBox's ItemsSource directly against the whole file: the count is the true total
// line count, and the indexer lazily reads a single line from the memory-mapped file on demand.
// The read-only IList + INotifyCollectionChanged surface and the empty-once-disposed safety live
// in MemoryMappedCollectionBase; this only supplies the live count, item materialization, and the
// background growth notifications.
public sealed class MemoryMappedFileLineCollection : MemoryMappedCollectionBase
{
    private const int CacheCapacity = 1000;
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(120);

    private readonly FileOffsetIndex index;
    private readonly MMapFile mmap;
    private readonly Dictionary<int, LinkedListNode<(int Index, MemoryMappedFileVisibleLine Line)>> cache = new();
    private readonly LinkedList<(int Index, MemoryMappedFileVisibleLine Line)> cacheOrder = new();

    private DispatcherTimer? growthTimer;
    private int notifiedCount;

    public MemoryMappedFileLineCollection(FileOffsetIndex index, MMapFile mmap)
    {
        this.index = index;
        this.mmap = mmap;
        notifiedCount = index.LineCount;

        if (!index.IsComplete)
            StartGrowthMonitor();
    }

    protected override int GetCount() => index.LineCount;

    protected override object GetItem(int index) => GetLine(index);

    private MemoryMappedFileVisibleLine GetLine(int i)
    {
        if (cache.TryGetValue(i, out var node))
        {
            cacheOrder.Remove(node);
            cacheOrder.AddFirst(node);
            return node.Value.Line;
        }

        int lineCount = index.LineCount;
        if (i < 0 || i >= lineCount)
            return new MemoryMappedFileVisibleLine(i + 1, string.Empty);

        var lineSpan = index.GetLineSpan(i);
        var line = new MemoryMappedFileVisibleLine(i + 1, NdJsonLineReader.ReadLine(mmap, lineSpan));

        var newNode = new LinkedListNode<(int, MemoryMappedFileVisibleLine)>((i, line));
        cacheOrder.AddFirst(newNode);
        cache[i] = newNode;

        if (cache.Count > CacheCapacity)
        {
            var lru = cacheOrder.Last!;
            cacheOrder.RemoveLast();
            cache.Remove(lru.Value.Index);
        }
        
        return line;
    }

    private void StartGrowthMonitor()
    {
        growthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = GrowthPollInterval };
        growthTimer.Tick += OnGrowthTick;
        growthTimer.Start();
    }

    private void OnGrowthTick(object? sender, EventArgs e)
    {
        int current = index.LineCount;
        bool complete = index.IsComplete;

        if (current > notifiedCount)
        {
            int delta = current - notifiedCount;
            // Placeholder entries only — the panel re-queries via the indexer when it
            // actually realizes a row, so eagerly reading/decoding every new line here
            // (which can number in the millions between ticks) would defeat the point.
            var newItems = new object?[delta];
            int startingIndex = notifiedCount;
            notifiedCount = current;
            RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newItems, startingIndex));
        }

        if (complete)
        {
            growthTimer!.Stop();
            growthTimer.Tick -= OnGrowthTick;
            growthTimer = null;
        }
    }

    protected override void DisposeCore()
    {
        if (growthTimer is not null)
        {
            growthTimer.Stop();
            growthTimer.Tick -= OnGrowthTick;
            growthTimer = null;
        }
    }
}
