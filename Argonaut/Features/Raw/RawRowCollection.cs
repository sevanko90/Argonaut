using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Threading;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Model for one visible display row of the raw viewer. <see cref="LineNumber"/> is null on
/// continuation rows (the left gutter stays blank); <see cref="IsSoftWrapped"/> drives the
/// return-symbol right gutter.
/// </summary>
public sealed class RawVisibleRow
{
    public RawVisibleRow(int? lineNumber, string text, bool isSoftWrapped)
    {
        LineNumber = lineNumber;
        Text = text;
        IsSoftWrapped = isSoftWrapped;
    }

    public int? LineNumber { get; }

    public string Text { get; }

    public bool IsSoftWrapped { get; }
}

// Backs the ListBox's ItemsSource directly against the whole file: the count is the live
// segment count, and the indexer lazily reads a single row from the memory-mapped file on
// demand. The read-only IList + INotifyCollectionChanged surface and the empty-once-disposed
// safety live in MemoryMappedCollectionBase; this only supplies the live count, item
// materialization, and the background growth notifications (structural twin of
// MemoryMappedFileLineCollection - see the growth-tick note there).
public sealed class RawRowCollection : MemoryMappedCollectionBase
{
    private const int CacheCapacity = 1000;
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(120);

    private readonly RawSegmentIndex index;
    private readonly MMapFile mmap;
    private readonly Dictionary<int, LinkedListNode<(int Index, RawVisibleRow Row)>> cache = new();
    private readonly LinkedList<(int Index, RawVisibleRow Row)> cacheOrder = new();

    private DispatcherTimer? growthTimer;
    private int notifiedCount;

    /// <summary>
    /// Test seam: rows materialized (cache misses) since construction. Headless UI tests
    /// assert this stays viewport-sized - a walk materializing every row of a multi-GB-backed
    /// source is exactly the runaway-memory failure mode the virtualization must prevent.
    /// </summary>
    internal int MaterializedRowCount;

    public RawRowCollection(RawSegmentIndex index, MMapFile mmap)
    {
        this.index = index;
        this.mmap = mmap;
        notifiedCount = index.RowCount;

        if (!index.IsComplete)
            StartGrowthMonitor();
    }

    protected override int GetCount() => index.RowCount;

    protected override object GetItem(int index) => GetRow(index);

    private RawVisibleRow GetRow(int i)
    {
        if (cache.TryGetValue(i, out var node))
        {
            cacheOrder.Remove(node);
            cacheOrder.AddFirst(node);
            return node.Value.Row;
        }

        int rowCount = index.RowCount;
        if (i < 0 || i >= rowCount)
            return new RawVisibleRow(null, string.Empty, false);

        MaterializedRowCount++;
        var info = index.GetRowInfo(i);
        var row = new RawVisibleRow(
            info.LineNumber,
            RawRowReader.ReadRow(mmap, info.Start, info.End, info.IsSoftWrapped),
            info.IsSoftWrapped);

        var newNode = new LinkedListNode<(int, RawVisibleRow)>((i, row));
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

    private void StartGrowthMonitor()
    {
        growthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = GrowthPollInterval };
        growthTimer.Tick += OnGrowthTick;
        growthTimer.Start();
    }

    private void OnGrowthTick(object? sender, EventArgs e)
    {
        int current = index.RowCount;
        bool complete = index.IsComplete;

        if (current > notifiedCount)
        {
            int delta = current - notifiedCount;
            int startingIndex = notifiedCount;
            notifiedCount = current;
            // Placeholder entries only - the panel re-queries realized rows through the
            // indexer (see MemoryMappedFileLineCollection.OnGrowthTick). Backed by a countful
            // stand-in rather than a real array: mid-scan deltas run to millions of rows, and
            // a real object?[] per tick is a large-object-heap allocation 8x/second for the
            // whole scan - GBs of garbage on a multi-GB file.
            RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, new NullPlaceholderList(delta), startingIndex));
        }

        if (complete)
        {
            growthTimer!.Stop();
            growthTimer.Tick -= OnGrowthTick;
            growthTimer = null;
        }
    }

    /// <summary>
    /// Allocation-free stand-in for a growth notification's "new items" list: a count of
    /// nulls. Consumers of the Add event only need the count and starting index to extend
    /// their row accounting; any that do read the list see the same nulls a placeholder
    /// array would have held.
    /// </summary>
    internal sealed class NullPlaceholderList : IList
    {
        private readonly int count;

        public NullPlaceholderList(int count) => this.count = count;

        public int Count => count;
        public object? this[int index]
        {
            get => (uint)index < (uint)count ? null : throw new ArgumentOutOfRangeException(nameof(index));
            set => throw new NotSupportedException();
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < count; i++)
                yield return null;
        }

        public void CopyTo(Array array, int index)
        {
            for (int i = 0; i < count; i++)
                array.SetValue(null, index + i);
        }

        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public bool Contains(object? value) => value is null && count > 0;
        public int IndexOf(object? value) => value is null && count > 0 ? 0 : -1;
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
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
