using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Threading;
using Argonaut.Infrastructure;

namespace Argonaut.Features.NdJson;

/// <summary>
/// One line as the NDJSON list renders it: its 1-based number and its display text.
/// </summary>
public sealed class NdJsonVisibleLine
{
    public NdJsonVisibleLine(int lineNumber, string text)
    {
        LineNumber = lineNumber;
        Text = text;
    }

    public int LineNumber { get; }

    public string Text { get; }
}

// Backs the ListBox's ItemsSource directly against the whole document: the count is the true
// total line count, and the indexer lazily reads a single line from the byte source on demand.
// The read-only IList + INotifyCollectionChanged surface and the empty-once-disposed safety live
// in VirtualizingItemsSourceBase; this only supplies the live count, item materialization, and the
// background growth notifications.
public sealed class NdJsonLineCollection : VirtualizingItemsSourceBase
{
    private const int CacheCapacity = 1000;
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(120);

    private readonly FileOffsetIndex index;
    private readonly IByteSource bytes;
    private readonly Dictionary<int, LinkedListNode<(int Index, NdJsonVisibleLine Line)>> cache = new();
    private readonly LinkedList<(int Index, NdJsonVisibleLine Line)> cacheOrder = new();

    private DispatcherTimer? growthTimer;
    private int notifiedCount;

    public NdJsonLineCollection(FileOffsetIndex index, IByteSource bytes)
    {
        this.index = index;
        this.bytes = bytes;
        notifiedCount = index.LineCount;

        if (!index.AllItemsPublished)
            StartGrowthMonitor();
    }

    protected override int GetCount() => index.LineCount;

    protected override object GetItem(int index) => GetLine(index);

    private NdJsonVisibleLine GetLine(int i)
    {
        if (cache.TryGetValue(i, out var node))
        {
            cacheOrder.Remove(node);
            cacheOrder.AddFirst(node);
            return node.Value.Line;
        }

        int lineCount = index.LineCount;
        if (i < 0 || i >= lineCount)
            return new NdJsonVisibleLine(i + 1, string.Empty);

        var lineSpan = index.GetLineSpan(i);
        var line = new NdJsonVisibleLine(i + 1, NdJsonLineReader.ReadDisplayLine(bytes, lineSpan));

        var newNode = new LinkedListNode<(int, NdJsonVisibleLine)>((i, line));
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
        bool complete = index.AllItemsPublished;

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
