using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Display model for one visible row: either a real token (value, or container start
/// shown collapsed/expanded) or a synthetic "N more items" placeholder for a container
/// whose direct-child count exceeds the display cap.
/// </summary>
public sealed class JsonRow
{
    public JsonRow(int position, int tokenIndex, int depth, JsonTokenKind kind, string? name, string value, bool hasChildren, bool isExpanded, bool isPlaceholder, string? hint = null, string? truncationHint = null, long? truncatedValueOffset = null, int? arrayIndex = null, string? schemaTitle = null, string? schemaDescription = null, string? schemaLabel = null)
    {
        SchemaTitle = schemaTitle;
        SchemaDescription = schemaDescription;
        SchemaLabel = schemaLabel;
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
        TruncatedValueOffset = truncatedValueOffset;
        ArrayIndex = arrayIndex;
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

    /// <summary>Zero-based position among this row's array siblings, or null when its
    /// parent isn't an array (an object member, or the document root) - drives the small
    /// index label shown to the left of the expander for array elements only.</summary>
    public int? ArrayIndex { get; }

    /// <summary>Muted decoded-value hint (e.g. a decoded date) to render after Value, or null.</summary>
    public string? Hint { get; }

    /// <summary>The bound schema's <c>title</c> for this row (or an enum member's label), or null
    /// when the schema documents no title. This is the *real* title only - the description
    /// fallback lives on <see cref="SchemaLabel"/> - so the tooltip can tell "titled" apart from
    /// "described" and only draw its title/description separator when there genuinely are
    /// both.</summary>
    public string? SchemaTitle { get; }

    /// <summary>The schema's <c>description</c> for this row, shown under the title in the schema
    /// gutter's tooltip. Null when the schema documents no description.</summary>
    public string? SchemaDescription { get; }

    /// <summary>What the schema gutter renders on this row: <see cref="SchemaTitle"/>, falling
    /// back to the first line of <see cref="SchemaDescription"/> for the (common) generated-schema
    /// case of a described-but-untitled property. Null when the schema says nothing here, which is
    /// also what blanks the gutter cell. Kept separate from <see cref="Hint"/> so a row can carry
    /// both a decoded date and a schema label.</summary>
    public string? SchemaLabel { get; }

    /// <summary>Drives the tooltip's title/description separator: only a row carrying both needs
    /// a rule between them.</summary>
    public bool HasSchemaTitleAndDescription => SchemaTitle is not null && SchemaDescription is not null;

    /// <summary>Whether this row opens a container, so the schema gutter can render its label as a
    /// heading over the child labels indented beneath it. Excludes placeholder rows, which borrow
    /// their container's <see cref="Kind"/> but describe a display cap rather than the container
    /// itself (they carry no schema label either way, so this only guards against later misuse).
    /// Closing-bracket rows are <c>EndObject</c>/<c>EndArray</c> and so are excluded already.</summary>
    public bool IsContainerRow => !IsPlaceholder && Kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    /// <summary>Muted note that Name and/or Value was display-capped (with the full length), or null.</summary>
    public string? TruncationHint { get; }

    /// <summary>Byte offset of the overflowing value's content in the file, set only when
    /// Value (not just Name) was truncated - lets the view offer a "view in raw" jump to
    /// where the full value actually starts. Null otherwise.</summary>
    public long? TruncatedValueOffset { get; }

    // Scalar-kind flags consumed by JsonView.axaml Classes.* bindings for per-type value
    // coloring. Container, placeholder and summary rows match none of them and keep the
    // default foreground.
    public bool IsStringValue => Kind == JsonTokenKind.String;
    public bool IsNumberValue => Kind == JsonTokenKind.Number;
    public bool IsBooleanValue => Kind is JsonTokenKind.True or JsonTokenKind.False;
    public bool IsNullValue => Kind == JsonTokenKind.Null;

    // Split for JsonView.axaml: TruncationHint alone can't tell a plain informational note
    // (name-only truncation, nothing to jump to) apart from one that should render as a
    // clickable "view in raw" link (value truncation, which always carries an offset).
    public bool ShowPlainTruncationHint => TruncationHint is not null && TruncatedValueOffset is null;
    public bool ShowTruncationLink => TruncatedValueOffset is not null;
}

/// <summary>
/// Lazily-decoded, expand/collapse-aware flattened projection of a JsonStructureIndex,
/// backing the JSON tree ListBox directly. No node text is decoded until a row is
/// actually realized, and only currently-expanded subtrees (capped per container) are
/// ever materialized into the visible list - the rest of a huge document is never touched.
/// </summary>
public sealed class JsonVisibleRowCollection : MemoryMappedCollectionBase
{
    private const int ChildCap = 10_000;
    // Display cap for any one decoded text (a scalar value or a property name) - see
    // DisplayText for why every display path is capped. Rows past the cap render a
    // truncation hint carrying the token's real length instead.
    internal const int MaxDisplayTextLength = DisplayText.MaxLength;
    // Hard ceiling on how far repeated "show more" clicks can page a single container's
    // children into the visible list. Rebuild() re-walks the whole visible tree on every
    // toggle (see class remarks), so without this cap, paging through a container with
    // millions of children one "show more" click at a time degrades to O(n^2).
    private const int MaxDisplayedChildrenPerContainer = 20_000;
    private const int ChildCountCap = 50_000;
    private const int RowCacheCapacity = 1000;
    // Every tick that actually finds new tokens hands the visible ListBox a CollectionChanged
    // notification, which forces Avalonia's VirtualizingStackPanel to redo virtualization/
    // scroll-extent bookkeeping - and confirmed by testing, every single one of those
    // notifications produces one visible selection/hover-highlight glitch (a known Avalonia
    // panel bug around selection/hover state during virtualization; see e.g.
    // https://github.com/AvaloniaUI/Avalonia/issues/11666 and
    // https://github.com/AvaloniaUI/Avalonia/issues/17635). There's no interval that avoids it
    // entirely without giving up live row streaming during indexing (a deliberate feature -
    // the view must stay scrollable/readable while a multi-GB file is still indexing), so this
    // only trades glitch frequency against how live the view feels.
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(1500);

    private readonly JsonStructureIndex index;
    private readonly MMapFile mmap;
    private readonly IReadOnlyList<IValueHintProvider>? hintProviders;

    // The bound schema, or null. Resolution happens top-down during AppendSubtree (each row
    // inherits its parent's node id), never bottom-up from a row's path - see
    // JsonSchemaDocument's remarks for why. With no schema bound the whole feature costs one
    // `schemaNodeId < 0` test per row and never touches the mapping.
    private JsonSchemaDocument? schema;

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

    // Token indices of currently-visible collapsed containers whose EndIndex isn't known
    // yet, so their row still shows the "…" placeholder summary instead of a real child
    // count. Rebuild diffs this against the previous rebuild's set to tell "nothing about
    // the existing rows actually changed" apart from "a collapsed row's summary just
    // became available and needs a real refresh" - see Rebuild's isPureAppend check.
    private HashSet<int>? unsettledCollapsedContainerTokens;

    // True when the last Rebuild found nothing that could still change what's currently
    // visible: every visible container is either fully indexed (EndIndex known), or
    // collapsed/at its display cap so further growth stays hidden behind a summary/
    // placeholder that doesn't depend on how much more has been indexed (see
    // AppendSubtree). A huge top-level array reaches this quickly once its cap is hit,
    // even though it won't actually close until the whole file finishes - from that point,
    // growth ticks skip the rebuild instead of forcing the viewport to re-realize
    // everything on every poll for the rest of indexing.
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

    // Ceiling on how many visible rows one deep-expand may build. Rebuild and
    // FindVisiblePosition are both O(visible rows) and run on every subsequent toggle, so an
    // unbounded expand-all near the root of a huge file would tax every later click, not
    // just this one. Approximate: containers on the walk's stack when the budget runs out
    // are already flipped and still show up to their child cap, so the real row count can
    // overshoot by a bounded slack - fine for a UI guard.
    internal const int ExpandAllRowBudget = 100_000;

    /// <summary>
    /// Deep-toggles the container at the given row position (alt/option-click on its
    /// expander). Collapsed: expands it and every descendant container, materialised into
    /// expandOverrides by a walk that mirrors AppendSubtree's display caps (children behind
    /// a "more items" placeholder are not touched) and stops at ExpandAllRowBudget.
    /// Expanded: collapses it and forgets all descendant expand/paging state, so a later
    /// re-expand starts from the clean default. Placeholder rows fall back to the normal
    /// "show more" paging. Returns true when expansion stopped at the budget.
    /// </summary>
    public bool ToggleExpandAll(int position) => ToggleExpandAll(position, ExpandAllRowBudget);

    internal bool ToggleExpandAll(int position, int rowBudget)
    {
        if (position < 0 || position >= visibleRows.Count)
            return false;

        var vrow = visibleRows[position];

        if (vrow.IsPlaceholder)
        {
            ToggleExpand(position);
            return false;
        }

        var token = index.GetToken(vrow.TokenIndex);
        if (!IsContainer(token.Kind))
            return false;

        bool budgetHit = false;
        if (IsExpanded(vrow.TokenIndex, token.Depth))
        {
            CollapseAllDescendants(vrow.TokenIndex, token.EndIndex);
        }
        else
        {
            int budget = rowBudget;
            ExpandSubtree(vrow.TokenIndex, ref budget);
            budgetHit = budget <= 0;
        }

        Rebuild();
        return budgetHit;
    }

    /// <summary>
    /// Collapses the (currently expanded) container and purges every descendant's expand
    /// override and paging limit, so re-expanding shows the default-depth state again. For
    /// an unclosed container (EndIndex &lt; 0) every later indexed token is inside it, so
    /// the purge range is open-ended. O(override/paging entry count), never document size.
    /// </summary>
    private void CollapseAllDescendants(int tokenIndex, int endIndex)
    {
        if (!expandOverrides.Remove(tokenIndex))
            expandOverrides.Add(tokenIndex);

        expandOverrides.RemoveWhere(i => i > tokenIndex && (endIndex < 0 || i < endIndex));

        List<int>? stale = null;
        foreach (int key in expandedChildLimit.Keys)
        {
            if (key > tokenIndex && (endIndex < 0 || key < endIndex))
                (stale ??= new List<int>()).Add(key);
        }

        if (stale is not null)
        {
            foreach (int key in stale)
                expandedChildLimit.Remove(key);
        }
    }

    /// <summary>
    /// Flips every collapsed container in the subtree to expanded, walking exactly the
    /// children AppendSubtree would display (same per-container limit, same early-outs for
    /// unindexed regions) and decrementing <paramref name="budget"/> once per row the
    /// expansion will make visible. Stops descending once the budget is exhausted.
    /// </summary>
    private void ExpandSubtree(int tokenIndex, ref int budget)
    {
        if (budget <= 0)
            return;
        budget--; // the row for this token itself

        var token = index.GetToken(tokenIndex);
        if (!IsContainer(token.Kind))
            return;

        if (!IsExpanded(tokenIndex, token.Depth))
        {
            if (!expandOverrides.Remove(tokenIndex))
                expandOverrides.Add(tokenIndex);
        }

        int limit = expandedChildLimit.TryGetValue(tokenIndex, out var l) ? l : ChildCap;
        int childIndex = tokenIndex + 1;
        int containerEnd = token.EndIndex;
        int shown = 0;

        while (true)
        {
            if (containerEnd >= 0 && childIndex >= containerEnd)
            {
                budget--; // closing-bracket row
                return;
            }

            if (childIndex >= index.TokenCount)
                return; // indexing hasn't reached here yet

            if (shown >= limit)
            {
                budget--; // "more items" placeholder row
                return;
            }

            if (budget <= 0)
                return;

            var child = index.GetToken(childIndex);
            ExpandSubtree(childIndex, ref budget);
            shown++;

            if (IsContainer(child.Kind))
            {
                if (child.EndIndex < 0)
                    return; // child subtree not fully indexed - can't locate its sibling yet
                childIndex = child.EndIndex + 1;
            }
            else
            {
                childIndex++;
            }
        }
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

        string? schemaTitle = null;
        string? schemaDescription = null;
        string? schemaLabel = null;
        if (schema is not null && vrow.SchemaNodeId >= 0)
        {
            schemaTitle = schema.GetTitle(vrow.SchemaNodeId);
            schemaDescription = schema.GetDescription(vrow.SchemaNodeId);

            // Enum matching reuses the value string already decoded above - no extra mmap read,
            // no extra allocation - and a matched member label supersedes the node's own title,
            // since "Sold by third party" says more here than "Availability".
            if (!isContainer && schema.TryGetEnumLabel(vrow.SchemaNodeId, value, token.Kind, out var enumTitle, out var enumDescription))
            {
                schemaTitle = enumTitle ?? schemaTitle;
                schemaDescription = enumDescription ?? schemaDescription;
            }

            // A schema that documents a property with `description` and no `title` is the norm
            // rather than the exception once schemas are generated rather than hand-written: in a
            // real OpenAPI document 83% of documented properties carry a description only. Those
            // rows would otherwise show nothing at all in the gutter - and, since the gutter cell
            // is what carries the tooltip, their description would be unreachable too.
            schemaLabel = schemaTitle ?? FirstLine(schemaDescription);
        }

        string? truncationHint = valueTruncated
            ? $"(truncated — full length {FormatByteLength(token.Length)})"
            : nameTruncated
                ? $"(name truncated — full length {FormatByteLength(token.NameLength)})"
                : null;

        long? truncatedValueOffset = valueTruncated ? token.Offset : null;

        int? arrayIndex = vrow.ArrayIndex >= 0 ? vrow.ArrayIndex : null;

        return new JsonRow(position, vrow.TokenIndex, token.Depth, token.Kind, name, value, hasChildren, expanded, isPlaceholder: false, hint: hint, truncationHint: truncationHint, truncatedValueOffset: truncatedValueOffset, arrayIndex: arrayIndex, schemaTitle: schemaTitle, schemaDescription: schemaDescription, schemaLabel: schemaLabel);
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
        => DisplayText.Read(mmap, offset, length, out truncated, MaxDisplayTextLength);

    private static string FormatByteLength(int bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.#} GB"
    };

    /// <summary>
    /// The first line of a schema description, for use as a gutter label. Descriptions are prose
    /// and often several paragraphs (frequently with a trailing "Schema link: …"), so only the
    /// opening line is a candidate; the full text stays on the tooltip.
    ///
    /// The hard cap is a measuring guard, not the visible limit - the gutter cell trims to the
    /// gutter's current width and shows an ellipsis there - so nothing is cut short enough for the
    /// cap to be what the user sees. Returns the original string when neither applies, so the
    /// common short description costs no allocation.
    /// </summary>
    private const int MaxSchemaLabelLength = 200;

    private static string? FirstLine(string? description)
    {
        if (description is null)
            return null;

        int end = description.IndexOfAny(NewLineChars);

        // Descriptions written for a docs site break paragraphs with a literal line-break tag as
        // often as with a newline, and everything after the first break is no more wanted on the
        // row than a second paragraph would be. Both spellings occur - the real OpenAPI document
        // this was built against uses the (invalid, but common) closing form.
        int tag = EarliestOf(description, "<br", "</br");
        if (tag >= 0 && (end < 0 || tag < end))
            end = tag;

        if (end < 0)
            end = description.Length;
        if (end > MaxSchemaLabelLength)
            end = MaxSchemaLabelLength;

        return end == description.Length ? description : description[..end].TrimEnd();
    }

    private static readonly char[] NewLineChars = { '\r', '\n' };

    private static int EarliestOf(string text, string a, string b)
    {
        int first = text.IndexOf(a, StringComparison.OrdinalIgnoreCase);
        int second = text.IndexOf(b, StringComparison.OrdinalIgnoreCase);

        if (first < 0)
            return second;

        return second < 0 ? first : Math.Min(first, second);
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    /// <summary>
    /// Recomputes the whole visible row list from scratch by walking expanded subtrees,
    /// starting from the root token. Bounded by however many rows are currently visible
    /// (expanded containers x their child cap), never by total document size.
    /// </summary>
    private void Rebuild()
    {
        // Seeded from the outgoing list's size: Rebuild fires on every expand/collapse and
        // (while a huge file is still indexing) every growth-poll tick, so an unsized list
        // would repeatedly re-grow-by-doubling from empty for what's usually a similarly
        // sized visible set each time.
        var newVisible = new List<VisibleRow>(visibleRows.Count);
        visibleTreeSettled = index.TokenCount > 0; // AppendSubtree clears it on any incomplete container
        if (index.TokenCount > 0)
            AppendSubtree(0, newVisible, arrayIndex: -1, schemaNodeId: schema?.RootId ?? -1);

        var oldVisible = visibleRows;
        var oldUnsettledCollapsed = unsettledCollapsedContainerTokens;
        var newUnsettledCollapsed = CollectUnsettledCollapsedContainers(newVisible);

        // A growth-poll tick during indexing overwhelmingly just extends the visible list -
        // AppendSubtree resumes exactly where the previous walk ran out of indexed tokens -
        // without touching any row already shown. Detect that case and raise a targeted Add
        // instead of a Reset: Avalonia's ListBox clears SelectedIndex on Reset (see
        // JsonView.SyncVisualSelection's remarks), so firing one on every tick is what made
        // the selection highlight flicker while auto-expand kept several containers open
        // (and therefore still growing) for the whole indexing run. Any real structural
        // change - or a previously-collapsed row in the unchanged prefix having just become
        // fully indexed, so its "…" placeholder needs to turn into a real child count -
        // falls back to the full Reset so the row actually gets refreshed.
        bool isPureAppend = newVisible.Count > oldVisible.Count
            && IsPrefixOf(oldVisible, newVisible)
            && !AnyNowSettled(oldUnsettledCollapsed);

        visibleRows = newVisible;
        unsettledCollapsedContainerTokens = newUnsettledCollapsed;
        lastRebuildTokenCount = index.TokenCount;

        if (isPureAppend)
        {
            int startIndex = oldVisible.Count;
            RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, new object?[newVisible.Count - startIndex], startIndex));
            return;
        }

        rowCache.Clear();
        rowCacheOrder.Clear();

        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private static bool IsPrefixOf(List<VisibleRow> oldList, List<VisibleRow> newList)
    {
        for (int i = 0; i < oldList.Count; i++)
        {
            var a = oldList[i];
            var b = newList[i];
            if (a.TokenIndex != b.TokenIndex || a.PlaceholderContainerTokenIndex != b.PlaceholderContainerTokenIndex)
                return false;
        }

        return true;
    }

    private bool AnyNowSettled(HashSet<int>? previouslyUnsettled)
    {
        if (previouslyUnsettled is null)
            return false;

        foreach (int tokenIndex in previouslyUnsettled)
        {
            if (index.GetToken(tokenIndex).EndIndex >= 0)
                return true;
        }

        return false;
    }

    private HashSet<int>? CollectUnsettledCollapsedContainers(List<VisibleRow> rows)
    {
        HashSet<int>? set = null;
        foreach (var row in rows)
        {
            if (row.IsPlaceholder)
                continue;

            var token = index.GetToken(row.TokenIndex);
            if (token.EndIndex < 0 && IsContainer(token.Kind) && !IsExpanded(row.TokenIndex, token.Depth))
                (set ??= new HashSet<int>()).Add(row.TokenIndex);
        }

        return set;
    }

    private void AppendSubtree(int tokenIndex, List<VisibleRow> into, int arrayIndex, int schemaNodeId)
    {
        into.Add(VisibleRow.ForToken(tokenIndex, arrayIndex, schemaNodeId));

        var token = index.GetToken(tokenIndex);
        if (!IsContainer(token.Kind))
            return;

        if (!IsExpanded(tokenIndex, token.Depth))
        {
            // Collapsed: nothing below it is walked, so the only thing further indexing
            // could still change is this row's own summary text ("…" becomes a real count
            // once EndIndex is known - see BuildContainerSummary/DescribeChildCount).
            if (token.EndIndex < 0)
                visibleTreeSettled = false;
            return;
        }

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
                // without collapsing it back down. Closed and fully displayed - settled.
                into.Add(VisibleRow.ForToken(containerEnd, arrayIndex: -1, schemaNodeId: -1));
                return;
            }

            if (childIndex >= index.TokenCount)
            {
                // Indexing hasn't reached here yet, so this container is still genuinely
                // filling in - a later growth-poll Rebuild needs to catch up.
                visibleTreeSettled = false;
                return;
            }

            if (shown >= limit)
            {
                // At the display cap: BuildRow's "N more items"/"display limit reached"
                // placeholder text depends only on the cap vs expandedChildLimit, never on
                // how much more of the container has since been indexed, so nothing about
                // what's currently visible changes until the user clicks "show more" (its
                // own explicit Rebuild) - not a reason to keep polling this container.
                into.Add(VisibleRow.ForMorePlaceholder(tokenIndex));
                return;
            }

            var child = index.GetToken(childIndex);

            // One O(1) step down the schema, in lockstep with the step down the document. The
            // `schemaNodeId < 0` short-circuit is what keeps the no-schema case free, and it also
            // means that once the schema runs out below some subtree, -1 propagates and the whole
            // rest of that subtree costs nothing either.
            int childSchemaId = schemaNodeId < 0 || schema is null
                ? -1
                : token.Kind == JsonTokenKind.StartArray
                    ? schema.ResolveElement(schemaNodeId, shown)
                    : child.NameLength >= 0
                        ? schema.ResolveMember(schemaNodeId, mmap.GetSpan(child.NameOffset, child.NameLength))
                        : -1;

            AppendSubtree(childIndex, into, arrayIndex: token.Kind == JsonTokenKind.StartArray ? shown : -1, schemaNodeId: childSchemaId);
            shown++;

            if (IsContainer(child.Kind))
            {
                if (child.EndIndex < 0)
                {
                    // This child is within our own display budget (shown < limit) and its
                    // subtree isn't fully indexed, so we can't locate its sibling yet - but
                    // once child closes, a sibling within our remaining budget (or our own
                    // closing bracket) may still need to appear, so this container isn't
                    // settled yet even if child's own recursive call didn't need to say so
                    // (e.g. child is itself capped and therefore settled on its own terms).
                    visibleTreeSettled = false;
                    return;
                }

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
    /// <summary>
    /// Binds (or, with null, unbinds) a JSON Schema and rebuilds the visible list. Unlike a
    /// date-hint change - which only affects row *text*, so
    /// <see cref="InvalidateRealizedRows"/> suffices - schema node ids are resolved during the
    /// walk and stored in the visible rows themselves, so they can only be recomputed by
    /// re-walking. The structure is unchanged, so this lands on Rebuild's Reset path.
    /// </summary>
    public void SetSchema(JsonSchemaDocument? schema)
    {
        if (ReferenceEquals(this.schema, schema))
            return;

        this.schema = schema;
        Rebuild();
    }

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

        _ = AwaitIndexingCompletionAsync();
    }

    /// <summary>
    /// The growth-poll timer trades live-update frequency against a known Avalonia panel
    /// glitch (see GrowthPollInterval's remarks), which means a file that finishes indexing
    /// well within one poll interval would otherwise keep showing only the construction-time
    /// Rebuild until the next tick caught up - a multi-second wait on a file that's actually
    /// already fully indexed. Awaiting the indexing task directly - regardless of success,
    /// failure or cancellation, since MarkComplete runs in RunIndexing's finally either way -
    /// guarantees one immediate final rebuild the moment indexing actually stops, independent
    /// of the poll cadence. Called from StartGrowthMonitor, which only ever runs on the UI
    /// thread (JsonVisibleRowCollection is always constructed there), so this await resumes
    /// there too - no explicit dispatch needed.
    /// </summary>
    private async Task AwaitIndexingCompletionAsync()
    {
        try
        {
            await index.IndexingTask;
        }
        catch
        {
            // Failure/cancellation is already recorded via index.Failure; only IsComplete
            // (set unconditionally in RunIndexing's finally) matters here.
        }

        if (IsDisposed || growthTimer is null)
            return; // disposed, or a regular tick already ran the final rebuild and stopped this

        if (!visibleTreeSettled && index.TokenCount != lastRebuildTokenCount)
            Rebuild();

        growthTimer.Stop();
        growthTimer.Tick -= OnGrowthTick;
        growthTimer = null;
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
        private VisibleRow(int tokenIndex, int placeholderContainerTokenIndex, int arrayIndex, int schemaNodeId)
        {
            TokenIndex = tokenIndex;
            PlaceholderContainerTokenIndex = placeholderContainerTokenIndex;
            ArrayIndex = arrayIndex;
            SchemaNodeId = schemaNodeId;
        }

        public static VisibleRow ForToken(int tokenIndex, int arrayIndex = -1, int schemaNodeId = -1) => new(tokenIndex, -1, arrayIndex, schemaNodeId);

        public static VisibleRow ForMorePlaceholder(int containerTokenIndex) => new(-1, containerTokenIndex, -1, -1);

        public int TokenIndex { get; }

        public int PlaceholderContainerTokenIndex { get; }

        /// <summary>Zero-based position among this token's array siblings, or -1 if its
        /// parent isn't an array (an object member, or the document root) - see
        /// AppendSubtree, the only place that knows a child's ordinal for free while
        /// walking.</summary>
        public int ArrayIndex { get; }

        /// <summary>Node id in the bound <see cref="JsonSchemaDocument"/> describing this row, or
        /// -1 when no schema is bound or the schema says nothing about this position. Resolved
        /// once, during the walk that produced this row (see AppendSubtree), so BuildRow never
        /// has to work out where in the schema a row sits.</summary>
        public int SchemaNodeId { get; }

        public bool IsPlaceholder => TokenIndex < 0;
    }
}
