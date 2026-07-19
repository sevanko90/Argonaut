using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Threading;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Csv;

/// <summary>One displayed data row: 1-based row number plus its already-widthed cells.</summary>
public sealed class CsvVisibleRow
{
    public CsvVisibleRow(int rowNumber, IReadOnlyList<CsvCell> cells)
    {
        RowNumber = rowNumber;
        Cells = cells;
    }

    public int RowNumber { get; }

    public IReadOnlyList<CsvCell> Cells { get; }
}

// Adapted from Argonaut.Features.NdJson.MemoryMappedFileLineCollection: same IList +
// INotifyCollectionChanged + LRU-cache + growth-timer shape, so VirtualizingStackPanel only
// touches realized rows while FileOffsetIndex keeps indexing in the background. The one
// addition is dataStartIndex, which lets the "first row is header" tickbox shift which
// absolute line each virtual row index maps to without re-indexing the file.
public sealed class CsvRowCollection : IList, INotifyCollectionChanged, IDisposable
{
    private const int CacheCapacity = 1000;
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(120);

    private readonly FileOffsetIndex index;
    private readonly MMapFile mmap;
    private readonly byte delimiter;
    private readonly CsvColumnLayout layout;
    private readonly Dictionary<int, LinkedListNode<(int Index, CsvVisibleRow Row)>> cache = new();
    private readonly LinkedList<(int Index, CsvVisibleRow Row)> cacheOrder = new();

    private int dataStartIndex;
    private DispatcherTimer? growthTimer;
    private int notifiedCount;

    public CsvRowCollection(FileOffsetIndex index, MMapFile mmap, byte delimiter, CsvColumnLayout layout, int dataStartIndex)
    {
        this.index = index;
        this.mmap = mmap;
        this.delimiter = delimiter;
        this.layout = layout;
        this.dataStartIndex = dataStartIndex;
        notifiedCount = Count;

        if (!index.IsComplete)
            StartGrowthMonitor();
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => Math.Max(0, index.LineCount - dataStartIndex);

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int i]
    {
        get => GetRow(i);
        set => throw new NotSupportedException();
    }

    private CsvVisibleRow GetRow(int i)
    {
        if (cache.TryGetValue(i, out var node))
        {
            cacheOrder.Remove(node);
            cacheOrder.AddFirst(node);
            return node.Value.Row;
        }

        int count = Count;
        if (i < 0 || i >= count)
            return new CsvVisibleRow(i + 1, Array.Empty<CsvCell>());

        var lineSpan = index.GetLineSpan(i + dataStartIndex);
        var fields = CsvFieldReader.ReadFields(mmap, lineSpan, delimiter);
        var cells = new CsvCell[fields.Length];
        for (int c = 0; c < fields.Length; c++)
            cells[c] = new CsvCell(fields[c], layout.WidthFor(c));

        var row = new CsvVisibleRow(i + 1, cells);

        var newNode = new LinkedListNode<(int, CsvVisibleRow)>((i, row));
        cacheOrder.AddFirst(newNode);
        cache[i] = newNode;

        if (cache.Count > CacheCapacity)
        {
            var lru = cacheOrder.Last!;
            cacheOrder.RemoveLast();
            cache.Remove(lru.Value.Index);
        }

        return row;
    }

    /// <summary>
    /// Shifts which absolute file line virtual index 0 maps to (0 or 1, depending on whether
    /// row 0 is being treated as a header). Row identities shift, so cached rows are dropped and
    /// a Reset is raised rather than trying to patch indices in place.
    /// </summary>
    public void SetDataStartIndex(int newStart)
    {
        if (dataStartIndex == newStart)
            return;

        dataStartIndex = newStart;
        cache.Clear();
        cacheOrder.Clear();
        notifiedCount = Count;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void StartGrowthMonitor()
    {
        growthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = GrowthPollInterval };
        growthTimer.Tick += OnGrowthTick;
        growthTimer.Start();
    }

    private void OnGrowthTick(object? sender, EventArgs e)
    {
        int current = Count;
        bool complete = index.IsComplete;

        if (current > notifiedCount)
        {
            int delta = current - notifiedCount;
            // Placeholder entries only - the panel re-queries via the indexer when it
            // actually realizes a row, so eagerly reading/decoding every new row here
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
