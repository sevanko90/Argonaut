using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using Avalonia.Threading;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.Json;

/// <summary>
/// Display model for one visible row: either a real token (value, or container start
/// shown collapsed/expanded) or a synthetic "N more items" placeholder for a container
/// whose direct-child count exceeds the display cap.
/// </summary>
public sealed class JsonRow
{
    public JsonRow(int position, int tokenIndex, int depth, JsonTokenKind kind, string? name, string value, bool hasChildren, bool isExpanded, bool isPlaceholder)
    {
        Position = position;
        TokenIndex = tokenIndex;
        Depth = depth;
        Kind = kind;
        Name = name;
        Value = value;
        HasChildren = hasChildren;
        IsExpanded = isExpanded;
        IsPlaceholder = isPlaceholder;
    }

    /// <summary>Index into the owning JsonVisibleRowCollection's current visible list.</summary>
    public int Position { get; }
    public int TokenIndex { get; }
    public int Depth { get; }
    public JsonTokenKind Kind { get; }
    public string? Name { get; }
    public string Value { get; }
    public bool HasChildren { get; }
    public bool IsExpanded { get; }
    public bool IsPlaceholder { get; }
}

/// <summary>
/// Lazily-decoded, expand/collapse-aware flattened projection of a JsonStructureIndex,
/// backing the JSON tree ListBox directly. No node text is decoded until a row is
/// actually realized, and only currently-expanded subtrees (capped per container) are
/// ever materialized into the visible list - the rest of a huge document is never touched.
/// </summary>
public sealed class JsonVisibleRowCollection : IList, INotifyCollectionChanged, IDisposable
{
    private const int ChildCap = 2000;
    // Hard ceiling on how far repeated "show more" clicks can page a single container's
    // children into the visible list. Rebuild() re-walks the whole visible tree on every
    // toggle (see class remarks), so without this cap, paging through a container with
    // millions of children one "show more" click at a time degrades to O(n^2).
    private const int MaxDisplayedChildrenPerContainer = 20_000;
    private const int ChildCountCap = 50_000;
    private const int RowCacheCapacity = 1000;
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly JsonStructureIndex index;
    private readonly MMapFile mmap;
    private readonly HashSet<int> expandedTokenIndices = new();
    private readonly Dictionary<int, int> expandedChildLimit = new();

    private readonly Dictionary<int, LinkedListNode<(int Position, JsonRow Row)>> rowCache = new();
    private readonly LinkedList<(int Position, JsonRow Row)> rowCacheOrder = new();

    private List<VisibleRow> visibleRows = new();
    private DispatcherTimer? growthTimer;
    private int lastRebuildTokenCount = -1;

    public JsonVisibleRowCollection(JsonStructureIndex index, MMapFile mmap)
    {
        this.index = index;
        this.mmap = mmap;

        if (index.TokenCount > 0)
        {
            var root = index.GetToken(0);
            if (IsContainer(root.Kind))
                expandedTokenIndices.Add(0); // auto-expand root one level for a non-empty first view
        }

        Rebuild();

        if (!index.IsComplete)
            StartGrowthMonitor();
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => visibleRows.Count;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int i]
    {
        get => GetRow(i);
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Toggle expand/collapse of the container at the given row position, or - if the
    /// row is a "more items" placeholder - reveal the next batch of that container's
    /// children. No I/O or awaiting happens here: if the target region isn't indexed
    /// yet, the row simply shows nothing extra until the background growth poll catches
    /// up and rebuilds.
    /// </summary>
    public void ToggleExpand(int position)
    {
        if (position < 0 || position >= visibleRows.Count)
            return;

        var vrow = visibleRows[position];

        if (vrow.IsPlaceholder)
        {
            int containerTokenIndex = vrow.PlaceholderContainerTokenIndex;
            int currentLimit = expandedChildLimit.TryGetValue(containerTokenIndex, out var limit) ? limit : ChildCap;
            int newLimit = Math.Min(currentLimit + ChildCap, MaxDisplayedChildrenPerContainer);
            if (newLimit == currentLimit)
                return; // already at the display cap - nothing more to reveal

            expandedChildLimit[containerTokenIndex] = newLimit;
            Rebuild();
            return;
        }

        var token = index.GetToken(vrow.TokenIndex);
        if (!IsContainer(token.Kind))
            return;

        if (!expandedTokenIndices.Remove(vrow.TokenIndex))
            expandedTokenIndices.Add(vrow.TokenIndex);

        Rebuild();
    }

    private JsonRow GetRow(int position)
    {
        if (rowCache.TryGetValue(position, out var node))
        {
            rowCacheOrder.Remove(node);
            rowCacheOrder.AddFirst(node);
            return node.Value.Row;
        }

        var row = BuildRow(position, visibleRows[position]);

        var newNode = new LinkedListNode<(int, JsonRow)>((position, row));
        rowCacheOrder.AddFirst(newNode);
        rowCache[position] = newNode;

        if (rowCache.Count > RowCacheCapacity)
        {
            var lru = rowCacheOrder.Last!;
            rowCacheOrder.RemoveLast();
            rowCache.Remove(lru.Value.Position);
        }

        return row;
    }

    private JsonRow BuildRow(int position, VisibleRow vrow)
    {
        if (vrow.IsPlaceholder)
        {
            var container = index.GetToken(vrow.PlaceholderContainerTokenIndex);
            int currentLimit = expandedChildLimit.TryGetValue(vrow.PlaceholderContainerTokenIndex, out var l) ? l : ChildCap;
            bool canLoadMore = currentLimit < MaxDisplayedChildrenPerContainer;
            string text = canLoadMore
                ? "… more items (click to show more)"
                : $"… display limit reached ({MaxDisplayedChildrenPerContainer:N0} items shown)";
            return new JsonRow(position, vrow.PlaceholderContainerTokenIndex, container.Depth + 1, container.Kind,
                name: null, value: text,
                hasChildren: canLoadMore, isExpanded: false, isPlaceholder: true);
        }

        var token = index.GetToken(vrow.TokenIndex);
        string? name = token.NameLength >= 0 ? ReadText(token.NameOffset, token.NameLength) : null;
        bool isContainer = IsContainer(token.Kind);
        bool expanded = isContainer && expandedTokenIndices.Contains(vrow.TokenIndex);

        string value = isContainer
            ? BuildContainerSummary(vrow.TokenIndex, token, expanded)
            : BuildScalarText(token);

        bool hasChildren = isContainer && (token.EndIndex < 0 || token.EndIndex > vrow.TokenIndex + 1);

        return new JsonRow(position, vrow.TokenIndex, token.Depth, token.Kind, name, value, hasChildren, expanded, isPlaceholder: false);
    }

    private string BuildContainerSummary(int tokenIndex, JsonTokenInfo token, bool expanded)
    {
        string open = token.Kind == JsonTokenKind.StartObject ? "{" : "[";
        if (expanded)
            return open;

        string close = token.Kind == JsonTokenKind.StartObject ? "}" : "]";
        string countText = token.EndIndex >= 0 ? DescribeChildCount(tokenIndex, token) : "…";
        return $"{open} {countText} {close}";
    }

    private string DescribeChildCount(int containerTokenIndex, JsonTokenInfo container)
    {
        string label = container.Kind == JsonTokenKind.StartObject ? "member" : "item";
        int count = 0;
        int i = containerTokenIndex + 1;
        int end = container.EndIndex;

        while (i < end && count <= ChildCountCap)
        {
            var t = index.GetToken(i);
            count++;
            i = IsContainer(t.Kind) ? t.EndIndex + 1 : i + 1;
        }

        return count > ChildCountCap ? $"{ChildCountCap}+ {label}s" : $"{count} {label}{(count == 1 ? "" : "s")}";
    }

    private string BuildScalarText(JsonTokenInfo token) => token.Kind switch
    {
        JsonTokenKind.Null => "null",
        JsonTokenKind.True => "true",
        JsonTokenKind.False => "false",
        JsonTokenKind.Number => ReadText(token.Offset, token.Length),
        _ => "\"" + ReadText(token.Offset, token.Length) + "\""
    };

    private string ReadText(long offset, int length)
    {
        if (length <= 0)
            return string.Empty;

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int bytesRead = mmap.Read(offset, buffer, length);
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    /// <summary>
    /// Recomputes the whole visible row list from scratch by walking expanded subtrees,
    /// starting from the root token. Bounded by however many rows are currently visible
    /// (expanded containers x their child cap), never by total document size.
    /// </summary>
    private void Rebuild()
    {
        var newVisible = new List<VisibleRow>();
        if (index.TokenCount > 0)
            AppendSubtree(0, newVisible);

        visibleRows = newVisible;
        lastRebuildTokenCount = index.TokenCount;

        rowCache.Clear();
        rowCacheOrder.Clear();

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void AppendSubtree(int tokenIndex, List<VisibleRow> into)
    {
        into.Add(VisibleRow.ForToken(tokenIndex));

        var token = index.GetToken(tokenIndex);
        if (!IsContainer(token.Kind) || !expandedTokenIndices.Contains(tokenIndex))
            return;

        int limit = expandedChildLimit.TryGetValue(tokenIndex, out var l) ? l : ChildCap;
        int childIndex = tokenIndex + 1;
        int containerEnd = token.EndIndex;
        int shown = 0;

        while (true)
        {
            if (containerEnd >= 0 && childIndex >= containerEnd)
                return; // reached the container's own End token - all children shown

            if (childIndex >= index.TokenCount)
                return; // indexing hasn't reached here yet; a later growth-poll Rebuild will catch up

            if (shown >= limit)
            {
                into.Add(VisibleRow.ForMorePlaceholder(tokenIndex));
                return;
            }

            var child = index.GetToken(childIndex);
            AppendSubtree(childIndex, into);
            shown++;

            if (IsContainer(child.Kind))
            {
                if (child.EndIndex < 0)
                    return; // this child's own subtree isn't fully indexed - can't locate its sibling yet
                childIndex = child.EndIndex + 1;
            }
            else
            {
                childIndex++;
            }
        }
    }

    private void StartGrowthMonitor()
    {
        growthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = GrowthPollInterval };
        growthTimer.Tick += OnGrowthTick;
        growthTimer.Start();
    }

    private void OnGrowthTick(object? sender, EventArgs e)
    {
        bool complete = index.IsComplete;

        if (index.TokenCount != lastRebuildTokenCount)
            Rebuild();

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

    private readonly struct VisibleRow
    {
        private VisibleRow(int tokenIndex, int placeholderContainerTokenIndex)
        {
            TokenIndex = tokenIndex;
            PlaceholderContainerTokenIndex = placeholderContainerTokenIndex;
        }

        public static VisibleRow ForToken(int tokenIndex) => new(tokenIndex, -1);

        public static VisibleRow ForMorePlaceholder(int containerTokenIndex) => new(-1, containerTokenIndex);

        public int TokenIndex { get; }

        public int PlaceholderContainerTokenIndex { get; }

        public bool IsPlaceholder => TokenIndex < 0;
    }
}
