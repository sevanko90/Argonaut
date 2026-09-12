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
    public RawVisibleRow(int? lineNumber, string text, bool isSoftWrapped, long start, long end)
    {
        LineNumber = lineNumber;
        Text = text;
        IsSoftWrapped = isSoftWrapped;
        Start = start;
        End = end;
    }

    public int? LineNumber { get; }

    public string Text { get; }

    public bool IsSoftWrapped { get; }

    /// <summary>Absolute offset of the row's first byte. The caret and selection live in byte
    /// offsets, so a realized row has to carry its own range to be drawn against them.</summary>
    public long Start { get; }

    /// <summary>Exclusive end offset, including any newline bytes the row does not draw.</summary>
    public long End { get; }
}

// Lazily materializes rows against the whole file: the count is the live segment count, and the
// indexer reads a single row from the memory-mapped file on demand. RawTextSurface reads it by
// index for the rows it is drawing and subscribes to its growth notifications; the read-only
// IList + INotifyCollectionChanged surface and the empty-once-disposed safety live in
// VirtualizingItemsSourceBase (structural twin of NdJsonLineCollection - see the
// growth-tick note there).
public sealed class RawRowCollection : VirtualizingItemsSourceBase
{
    /// <summary>
    /// Rows kept decoded after they scroll away, so scrolling back does not re-read the mapping.
    /// The cost is fixed regardless of file size, and was measured full: at 1000 rows it held
    /// 720KB / 1014KB / 1656KB at wrap widths 80 / 160 / 512; at 200 it holds 154KB / 213KB /
    /// 346KB. A viewport is around 30 rows, so 200 is still several screens of scroll-back. The
    /// larger figure was sized for a ListBox realizing and de-realizing item containers as it
    /// scrolled; the surface draws directly and caches its own text layouts for the viewport.
    /// </summary>
    private const int CacheCapacity = 200;
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(120);

    private readonly RawSegmentIndex index;
    private readonly IByteSource bytes;
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

    public RawRowCollection(RawSegmentIndex index, IByteSource bytes)
    {
        this.index = index;
        this.bytes = bytes;
        notifiedCount = index.RowCount;

        if (!index.AllItemsPublished)
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
            return new RawVisibleRow(null, string.Empty, false, 0, 0);

        MaterializedRowCount++;
        var info = index.GetRowInfo(i);
        var row = new RawVisibleRow(
            info.LineNumber,
            RawRowReader.ReadRow(bytes, info.Start, info.End, info.IsSoftWrapped),
            info.IsSoftWrapped,
            info.Start,
            info.End);

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
        bool complete = index.AllItemsPublished;

        if (current > notifiedCount)
        {
            int delta = current - notifiedCount;
            int startingIndex = notifiedCount;
            notifiedCount = current;
            // Placeholder entries only - the panel re-queries realized rows through the
            // indexer (see NdJsonLineCollection.OnGrowthTick). Backed by a countful
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
