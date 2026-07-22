using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
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
    public JsonRow(int position, int tokenIndex, int depth, JsonTokenKind kind, string? name, string value, bool hasChildren, bool isExpanded, bool isPlaceholder, string? hint = null, string? truncationHint = null)
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
        TruncationHint = truncationHint;
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

    /// <summary>Muted note that Name and/or Value was display-capped (with the full length), or null.</summary>
    public string? TruncationHint { get; }
}

/// <summary>
/// Lazily-decoded, expand/collapse-aware flattened projection of a JsonStructureIndex,
/// backing the JSON tree ListBox directly. No node text is decoded until a row is
/// actually realized, and only currently-expanded subtrees (capped per container) are
/// ever materialized into the visible list - the rest of a huge document is never touched.
/// </summary>
public sealed class JsonVisibleRowCollection : MemoryMappedCollectionBase
{
    private const int ChildCap = 2000;
    // Display cap for any one decoded text (a scalar value or a property name). A single
    // pathologically large token (e.g. a 100 MB string) must never reach the TextBlock:
    // Avalonia lays out an unwrapped line in O(length), and the row would otherwise be
    // re-decoded in full on every growth-poll rebuild. Rows past the cap render a
    // truncation hint carrying the token's real length instead.
    internal const int MaxDisplayTextLength = 1024;
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

    // A container is expanded by default when its depth is below defaultExpandDepth; this
    // set holds only the containers where the user has explicitly toggled away from that
    // default (so a container is expanded iff (depth < defaultExpandDepth) XOR membership
    // here). Keeping expand state as a policy + override, rather than a plain "expanded"
    // set, means raising/lowering the default (e.g. via the header control) doesn't need to
    // touch every container - see SetDefaultExpandDepth.
    private readonly HashSet<int> expandOverrides = new();
    private readonly Dictionary<int, int> expandedChildLimit = new();
    private int defaultExpandDepth;

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

    public JsonVisibleRowCollection(JsonStructureIndex index, MMapFile mmap, IReadOnlyList<IValueHintProvider>? hintProviders = null, int defaultExpandDepth = 1)
    {
        this.index = index;
        this.mmap = mmap;
        this.defaultExpandDepth = Math.Max(0, defaultExpandDepth);
        this.hintProviders = hintProviders;

        if (hintProviders is not null)
        {
            foreach (var provider in hintProviders)
                provider.HintsChanged += OnHintsChanged;
        }

        Rebuild();

        if (!index.IsComplete)
            StartGrowthMonitor();
    }

    /// <summary>
    /// Changes how many container levels are expanded by default (0 = start fully collapsed)
    /// and rebuilds the visible list. Only affects containers the user hasn't explicitly
    /// expanded/collapsed themselves - see <see cref="IsExpanded"/>.
    /// </summary>
    public void SetDefaultExpandDepth(int depth)
    {
        depth = Math.Max(0, depth);
        if (depth == defaultExpandDepth)
            return;

        defaultExpandDepth = depth;
        Rebuild();
    }

    /// <summary>
    /// A container is expanded when its nesting depth is within the default-expand depth,
    /// unless the user has explicitly toggled it the other way (see expandOverrides).
    /// </summary>
    private bool IsExpanded(int tokenIndex, int depth) => (depth < defaultExpandDepth) ^ expandOverrides.Contains(tokenIndex);

    protected override int GetCount() => visibleRows.Count;

    protected override object GetItem(int index) => GetRow(index);

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

            var parentToken = index.GetToken(parentIndex);
            if (!IsExpanded(parentIndex, parentToken.Depth))
            {
                // Not currently expanded, so flipping the override always makes it expanded,
                // regardless of whether that means adding or removing membership.
                if (!expandOverrides.Remove(parentIndex))
                    expandOverrides.Add(parentIndex);
                changed = true;
            }

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

        if (!expandOverrides.Remove(vrow.TokenIndex))
            expandOverrides.Add(vrow.TokenIndex);

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
        bool nameTruncated = false;
        string? name = token.NameLength >= 0 ? ReadText(token.NameOffset, token.NameLength, out nameTruncated) : null;
        bool isContainer = IsContainer(token.Kind);
        bool expanded = isContainer && IsExpanded(vrow.TokenIndex, token.Depth);

        bool valueTruncated = false;
        string value = isContainer
            ? BuildContainerSummary(vrow.TokenIndex, token, expanded)
            : BuildScalarText(token, out valueTruncated);

        bool hasChildren = isContainer && (token.EndIndex < 0 || token.EndIndex > vrow.TokenIndex + 1);

        string? hint = isContainer ? null : BuildHint(vrow.TokenIndex, token);

        string? truncationHint = valueTruncated
            ? $"(truncated — full length {FormatByteLength(token.Length)})"
            : nameTruncated
                ? $"(name truncated — full length {FormatByteLength(token.NameLength)})"
                : null;

        return new JsonRow(position, vrow.TokenIndex, token.Depth, token.Kind, name, value, hasChildren, expanded, isPlaceholder: false, hint: hint, truncationHint: truncationHint);
    }

    private string? BuildHint(int tokenIndex, JsonTokenInfo token)
    {
        if (hintProviders is null)
            return null;

        // No classifiable value (a date in some encoding) is anywhere near this long; skip
        // early rather than hand providers a span over a pathologically large token.
        if (token.Length > MaxDisplayTextLength)
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

    private string BuildScalarText(JsonTokenInfo token, out bool truncated)
    {
        switch (token.Kind)
        {
            case JsonTokenKind.Null: truncated = false; return "null";
            case JsonTokenKind.True: truncated = false; return "true";
            case JsonTokenKind.False: truncated = false; return "false";
            case JsonTokenKind.EndObject: truncated = false; return "}";
            case JsonTokenKind.EndArray: truncated = false; return "]";
            case JsonTokenKind.Number: return ReadText(token.Offset, token.Length, out truncated);
            default:
                string text = ReadText(token.Offset, token.Length, out truncated);
                // A truncated string keeps its opening quote but gets no closing one: the
                // value visibly continues past the ellipsis. This also keeps copy-value's
                // quote stripping (first + last char) correct - it removes the quote and
                // the ellipsis, leaving exactly the truncated raw text.
                return truncated ? "\"" + text : "\"" + text + "\"";
        }
    }

    private string ReadText(long offset, int length, out bool truncated)
    {
        if (length <= 0)
        {
            truncated = false;
            return string.Empty;
        }

        if (length <= MaxDisplayTextLength)
        {
            truncated = false;
            return mmap.GetUtf8String(offset, length);
        }

        truncated = true;

        // Cut on a UTF-8 character boundary: read one byte past the cap and back the cut
        // off while the first excluded byte is a continuation byte (0b10xxxxxx), so a
        // multi-byte character is never split into a replacement glyph.
        var span = mmap.GetSpan(offset, MaxDisplayTextLength + 1);
        int cut = MaxDisplayTextLength;
        while (cut > 0 && (span[cut] & 0xC0) == 0x80)
            cut--;

        return Encoding.UTF8.GetString(span[..cut]) + "…";
    }

    private static string FormatByteLength(int bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.#} GB"
    };

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

        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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

        if (!IsExpanded(tokenIndex, token.Depth))
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
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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

    protected override void DisposeCore()
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
