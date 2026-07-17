using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Threading;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.NdJson;

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

// Backs the ListBox's ItemsSource directly against the whole file: Count is the true total
// line count, and the indexer lazily reads a single line from the memory-mapped file on
// demand. Avalonia's ItemsSourceView only avoids copying/enumerating the whole source into a
// List<object> when it implements non-generic IList (IReadOnlyList<T>/IList<T> alone do
// not qualify), so VirtualizingStackPanel touches only realized rows via Count + this[int].
public sealed class MemoryMappedFileLineCollection : IList, INotifyCollectionChanged, IDisposable
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

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => index.LineCount;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int i]
    {
        get => GetLine(i);
        set => throw new NotSupportedException();
    }

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
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newItems, startingIndex));
        }

        if (complete)
        {
            growthTimer!.Stop();
            growthTimer.Tick -= OnGrowthTick;
            growthTimer = null;
        }
    }

    public void Dispose()
    {
        if (growthTimer is not null)
        {
            growthTimer.Stop();
            growthTimer.Tick -= OnGrowthTick;
            growthTimer = null;
        }
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => false;

    public int IndexOf(object? value) => -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public IEnumerator GetEnumerator()
    {
        int count = Count;
        for (int i = 0; i < count; i++)
            yield return this[i];
    }
}
