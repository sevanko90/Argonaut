using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia.Threading;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Display model for one visible row: either a real token (value, or container start
/// shown collapsed/expanded) or a synthetic "N more items" placeholder for a container
/// whose direct-child count exceeds the display cap.
/// </summary>
public sealed class JsonRow
{
    public JsonRow(int position, int tokenIndex, int depth, JsonTokenKind kind, string? name, string value, bool hasChildren, bool isExpanded, bool isPlaceholder, string? hint = null)
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
        Hint = hint;
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

    /// <summary>Muted decoded-value hint (e.g. a decoded date) to render after Value, or null.</summary>
    public string? Hint { get; }
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
    private readonly IReadOnlyList<IValueHintProvider>? hintProviders;
    private readonly HashSet<int> expandedTokenIndices = new();
    private readonly Dictionary<int, int> expandedChildLimit = new();

    private readonly Dictionary<int, LinkedListNode<(int Position, JsonRow Row)>> rowCache = new();
    private readonly LinkedList<(int Position, JsonRow Row)> rowCacheOrder = new();

    // A container's direct-child count is immutable once its EndIndex is known (and
    // DescribeChildCount only runs then), so entries never need invalidating and the
    // cache intentionally survives Rebuild - without it, every collapsed container in
    // view recounts up to ChildCountCap tokens on every growth-poll rebuild.
    private readonly Dictionary<int, int> childCountCache = new();

    private List<VisibleRow> visibleRows = new();
    private DispatcherTimer? growthTimer;
    private int lastRebuildTokenCount = -1;

    // True when the last Rebuild saw every visible container fully indexed
    // (EndIndex known). From that point, further token growth cannot change any
    // visible row, so growth ticks skip the rebuild instead of forcing the viewport
    // to re-realize all visible text every 250ms for the rest of indexing.
    private bool visibleTreeSettled;

    public JsonVisibleRowCollection(JsonStructureIndex index, MMapFile mmap, IReadOnlyList<IValueHintProvider>? hintProviders = null)
    {
        this.index = index;
        this.mmap = mmap;
        this.hintProviders = hintProviders;

        if (hintProviders is not null)
        {
            foreach (var provider in hintProviders)
                provider.HintsChanged += OnHintsChanged;
        }

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
    /// Finds a token's current position in the visible list, or null if it isn't visible
    /// right now (its container is collapsed, or it hasn't been paged/indexed in yet).
    /// Linear scan bounded by how many rows are currently visible, never by document size -
    /// used to restore the selection highlight after a Rebuild reshuffles positions.
    /// </summary>
    public int? FindVisiblePosition(int tokenIndex)
    {
        for (int i = 0; i < visibleRows.Count; i++)
        {
            if (visibleRows[i].TokenIndex == tokenIndex)
                return i;
        }

        return null;
    }

    /// <summary>
    /// Ensures a token is reachable in the visible list by expanding every collapsed
    /// ancestor container along its ParentIndex chain and paging each ancestor's
    /// child-display limit up far enough to include it (capped at
    /// MaxDisplayedChildrenPerContainer, same ceiling repeated "show more" clicks respect).
    /// Every ancestor on this chain is necessarily a container, since only containers have
    /// children. Only touches ancestors of tokenIndex - O(depth) plus one O(preceding-
    /// siblings) sibling-skip walk per ancestor (the same technique
    /// JsonPathBuilder.FindArrayIndex uses to label path segments) - and skips Rebuild
    /// entirely if nothing actually needed to change, e.g. tokenIndex was already visible.
    /// </summary>
    public void EnsureVisible(int tokenIndex)
    {
        bool changed = false;
        int current = tokenIndex;

        while (true)
        {
            var token = index.GetToken(current);
            int parentIndex = token.ParentIndex;
            if (parentIndex == -1)
                break;

            if (expandedTokenIndices.Add(parentIndex))
                changed = true;

            // Applies to object parents as well as arrays: a target member past the child
            // cap needs the same paging-up or the expanded ancestors still won't show it.
            int childPosition = FindChildPosition(parentIndex, current);
            int currentLimit = expandedChildLimit.TryGetValue(parentIndex, out var l) ? l : ChildCap;

            if (childPosition >= currentLimit)
            {
                int neededLimit = Math.Min(
                    MaxDisplayedChildrenPerContainer,
                    ((childPosition / ChildCap) + 1) * ChildCap);

                if (neededLimit > currentLimit)
                {
                    expandedChildLimit[parentIndex] = neededLimit;
                    changed = true;
                }
            }

            current = parentIndex;
        }

        if (changed)
            Rebuild();
    }

    /// <summary>
    /// Finds the zero-based position of targetTokenIndex among its parent container's
    /// direct children, skipping whole sibling subtrees in O(1) via each sibling's
    /// EndIndex - the same pattern JsonPathBuilder.FindArrayIndex uses.
    /// </summary>
    private int FindChildPosition(int parentIndex, int targetTokenIndex)
    {
        int i = parentIndex + 1;
        int position = 0;

        while (i < targetTokenIndex)
        {
            var sibling = index.GetToken(i);
            i = IsContainer(sibling.Kind) ? sibling.EndIndex + 1 : i + 1;
            position++;
        }

        return position;
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

        string? hint = isContainer ? null : BuildHint(vrow.TokenIndex, token);

        return new JsonRow(position, vrow.TokenIndex, token.Depth, token.Kind, name, value, hasChildren, expanded, isPlaceholder: false, hint: hint);
    }

    private string? BuildHint(int tokenIndex, JsonTokenInfo token)
    {
        if (hintProviders is null)
            return null;

        foreach (var provider in hintProviders)
        {
            if (!provider.IsActive)
                continue;

            if (provider.TryClassify(token.Kind, mmap.GetSpan(token.Offset, token.Length), out var candidate))
            {
                string? hint = provider.FormatHint(in candidate, tokenIndex);
                if (hint is not null)
                    return hint;
            }
        }

        return null;
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

        if (!childCountCache.TryGetValue(containerTokenIndex, out int count))
        {
            int i = containerTokenIndex + 1;
            int end = container.EndIndex;

            while (i < end && count <= ChildCountCap)
            {
                var t = index.GetToken(i);
                count++;
                i = IsContainer(t.Kind) ? t.EndIndex + 1 : i + 1;
            }

            childCountCache[containerTokenIndex] = count;
        }

        return count > ChildCountCap ? $"{ChildCountCap}+ {label}s" : $"{count} {label}{(count == 1 ? "" : "s")}";
    }

    private string BuildScalarText(JsonTokenInfo token) => token.Kind switch
    {
        JsonTokenKind.Null => "null",
        JsonTokenKind.True => "true",
        JsonTokenKind.False => "false",
        JsonTokenKind.Number => ReadText(token.Offset, token.Length),
        JsonTokenKind.EndObject => "}",
        JsonTokenKind.EndArray => "]",
        _ => "\"" + ReadText(token.Offset, token.Length) + "\""
    };

    private string ReadText(long offset, int length)
    {
        if (length <= 0)
            return string.Empty;

        return mmap.GetUtf8String(offset, length);
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
        visibleTreeSettled = index.TokenCount > 0; // AppendSubtree clears it on any incomplete container
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
        if (!IsContainer(token.Kind))
            return;

        // An unclosed visible container means later token growth can still change this
        // row (its collapsed summary, hasChildren, or - if expanded - its child list).
        // Every early return below for "not indexed yet" is under an EndIndex < 0
        // container, so this single check captures all of them.
        if (token.EndIndex < 0)
            visibleTreeSettled = false;

        if (!expandedTokenIndices.Contains(tokenIndex))
            return;

        int limit = expandedChildLimit.TryGetValue(tokenIndex, out var l) ? l : ChildCap;
        int childIndex = tokenIndex + 1;
        int containerEnd = token.EndIndex;
        int shown = 0;

        while (true)
        {
            if (containerEnd >= 0 && childIndex >= containerEnd)
            {
                // Show the container's own closing bracket as its own row, at the same
                // depth as the opening one, so an expanded container's extent is visible
                // without collapsing it back down.
                into.Add(VisibleRow.ForToken(containerEnd));
                return;
            }

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

    /// <summary>
    /// Invalidates only realized row text (hints), keeping the visible-row structure: scheme
    /// changes can't add/remove rows, so clearing the LRU cache and firing Reset is sufficient
    /// - the ListBox re-realizes the viewport and BuildRow re-formats hints under the new
    /// settings.
    /// </summary>
    public void InvalidateRealizedRows()
    {
        rowCache.Clear();
        rowCacheOrder.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void OnHintsChanged(object? sender, EventArgs e) => InvalidateRealizedRows();

    private void StartGrowthMonitor()
    {
        growthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = GrowthPollInterval };
        growthTimer.Tick += OnGrowthTick;
        growthTimer.Start();
    }

    private void OnGrowthTick(object? sender, EventArgs e)
    {
        bool complete = index.IsComplete;

        // Once the visible tree is settled, token growth can't change any visible row,
        // so skip the rebuild (and its Reset event, which forces the viewport to
        // re-realize everything). ToggleExpand into an unindexed region re-clears the
        // flag via its own Rebuild, so ticks resume rebuilding when it matters again.
        if (!visibleTreeSettled && index.TokenCount != lastRebuildTokenCount)
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

        if (hintProviders is not null)
        {
            foreach (var provider in hintProviders)
                provider.HintsChanged -= OnHintsChanged;
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
