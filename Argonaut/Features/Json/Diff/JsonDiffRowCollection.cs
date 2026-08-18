using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Expand/collapse-aware flattened projection of a <see cref="JsonDiffIndex"/>'s record
/// log, backing the diff ListBox - one merged list, each row carrying both sides (diff
/// plan stage 5's "one list, not two synced panes"). The walk is a straight iteration
/// over the records, which are already in merged render order; it deliberately does NOT
/// share JsonVisibleRowCollection's AppendSubtree, whose walk is driven by one index's
/// parent chain rather than by alignment.
///
/// Undescended regions (Unchanged/Added/Removed/Moved subtrees - single records by
/// design) expand into token-level sub-rows walked straight off the relevant side's
/// index: left for Removed, right for Added, left mirrored into both panes for
/// Unchanged/Moved (their content is semantically identical on both sides; the right
/// pane then shows the left document's key order - a documented v1 simplification).
///
/// Before the first diff record exists (both documents still indexing), the collection
/// renders a live left-document preview so the view is never an empty pane with a
/// spinner; the right pane fills in when the diff starts streaming.
/// </summary>
public sealed class JsonDiffRowCollection : MemoryMappedCollectionBase
{
    private const int ChildCap = 10_000;
    private const int RowCacheCapacity = 1000;
    // Same interval/rationale as JsonVisibleRowCollection.GrowthPollInterval.
    private static readonly TimeSpan GrowthPollInterval = TimeSpan.FromMilliseconds(1500);

    private enum RowKind : byte
    {
        Record,
        SubLeft,    // left side only (removed subtrees, preview)
        SubRight,   // right side only (added subtrees)
        SubMirror,  // left content rendered into both panes (unchanged/moved subtrees)
        Placeholder
    }

    private readonly struct DiffVisibleRow
    {
        public DiffVisibleRow(RowKind kind, int recordIndex, int token, ushort depth, int arrayIndex, DiffStatus tint)
        {
            Kind = kind;
            RecordIndex = recordIndex;
            Token = token;
            Depth = depth;
            ArrayIndex = arrayIndex;
            Tint = tint;
        }

        public RowKind Kind { get; }
        public int RecordIndex { get; }   // -1 for sub/preview/placeholder rows
        public int Token { get; }         // sub rows: the token on their side; -1 otherwise
        public ushort Depth { get; }
        public int ArrayIndex { get; }    // sub rows: ordinal among array siblings, or -1
        public DiffStatus Tint { get; }   // sub rows inherit their region's status
    }

    private readonly JsonDiffSession session;
    private readonly JsonRowFactory leftFactory;
    private readonly JsonRowFactory rightFactory;

    // Expand state, override-over-default like the JSON view: records by record index,
    // token-level sub-rows by token index per side (a token appears under at most one
    // region, except a cross-parent move's two ends - which then share state, harmlessly).
    private readonly HashSet<int> recordOverrides = new();
    private readonly HashSet<int> leftTokenOverrides = new();
    private readonly HashSet<int> rightTokenOverrides = new();

    private readonly LruCache<int, JsonDiffRow> rowCache = new(RowCacheCapacity);
    private List<DiffVisibleRow> visibleRows = new();
    private IndexGrowthMonitor? growthMonitor;
    private (int Records, int LeftTokens) lastRebuildCounts = (-1, -1);
    private bool finalRebuildDone;
    private bool changesOnly;

    public JsonDiffRowCollection(JsonDiffSession session)
    {
        this.session = session;
        this.leftFactory = new JsonRowFactory(session.Left.Index, session.Left.File, hintProviders: null);
        this.rightFactory = new JsonRowFactory(session.Right.Index, session.Right.File, hintProviders: null);

        Rebuild();

        if (!session.Diff.IsComplete)
        {
            growthMonitor = new IndexGrowthMonitor(GrowthPollInterval, session.Diff.IndexingTask,
                isComplete: () => session.Diff.IsComplete,
                refresh: RefreshIfGrown);
        }
    }

    /// <summary>"Changes only" filter: Unchanged records (and their sub-rows) drop out of
    /// the walk. Cheap - it is a predicate in the walk, not a second collection.</summary>
    public bool ChangesOnly
    {
        get => changesOnly;
        set
        {
            if (changesOnly == value)
                return;
            changesOnly = value;
            Rebuild();
        }
    }

    private void RefreshIfGrown()
    {
        if (IsDisposed)
            return;

        var counts = (session.Diff.RecordCount, session.Left.Index.TokenCount);
        // The move-reconciliation pass mutates records without growing the log, so the
        // completion refresh must rebuild once even when the counts are unchanged.
        bool completionPass = session.Diff.IsComplete && !finalRebuildDone;
        if (counts == lastRebuildCounts && !completionPass)
            return;

        if (session.Diff.IsComplete)
            finalRebuildDone = true;
        Rebuild();
    }

    protected override int GetCount() => visibleRows.Count;

    protected override object GetItem(int index) => GetRow(index);

    /// <summary>
    /// Toggle expand/collapse of the row at <paramref name="position"/>. Record rows key
    /// their override by record index, token sub-rows by token index on their side;
    /// placeholders don't toggle. Rebuilds synchronously, like the JSON view.
    /// </summary>
    public void ToggleExpand(int position)
    {
        if (position < 0 || position >= visibleRows.Count)
            return;

        var vrow = visibleRows[position];
        switch (vrow.Kind)
        {
            case RowKind.Record:
                var record = session.Diff.GetRecord(vrow.RecordIndex);
                if (!RecordHasChildren(record))
                    return;
                Toggle(recordOverrides, vrow.RecordIndex);
                break;

            case RowKind.SubLeft:
            case RowKind.SubMirror:
                if (!SideHasChildren(leftSide: true, vrow.Token))
                    return;
                Toggle(leftTokenOverrides, vrow.Token);
                break;

            case RowKind.SubRight:
                if (!SideHasChildren(leftSide: false, vrow.Token))
                    return;
                Toggle(rightTokenOverrides, vrow.Token);
                break;

            default:
                return;
        }

        Rebuild();
    }

    private static void Toggle(HashSet<int> overrides, int key)
    {
        if (!overrides.Remove(key))
            overrides.Add(key);
    }

    private static bool IsContainer(JsonTokenKind kind) => kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray;

    private JsonStructureIndex SideIndex(bool leftSide) => leftSide ? session.Left.Index : session.Right.Index;

    private bool SideHasChildren(bool leftSide, int token)
    {
        var info = SideIndex(leftSide).GetToken(token);
        return IsContainer(info.Kind) && (info.EndIndex < 0 || info.EndIndex > token + 1);
    }

    /// <summary>Sub-rows and the preview default to "root level expanded, everything below
    /// collapsed", overridden per toggle - the same policy+override shape as the JSON view.</summary>
    private bool IsSubExpanded(HashSet<int> overrides, int token, int depth)
        => (depth < 1) ^ overrides.Contains(token);

    private bool RecordHasChildRecords(JsonDiffRecord record)
        => record.SubtreeEnd < 0 || record.SubtreeEnd > record.Index + 1;

    private bool RecordHasChildren(JsonDiffRecord record)
    {
        if (RecordHasChildRecords(record))
            return true;
        if (record.IsAlignmentApproximate)
            return false; // not descended, and token sub-walks of both sides would misalign
        if (record.Status is DiffStatus.Added)
            return SideHasChildren(leftSide: false, record.RightToken);
        if (record.LeftToken >= 0)
            return SideHasChildren(leftSide: true, record.LeftToken);
        return false;
    }

    /// <summary>Changed subtrees auto-expand down to the differing leaf (only changed paths
    /// were descended, so "expanded when descended" is exactly that); everything else
    /// starts collapsed behind its summary.</summary>
    private bool IsRecordExpanded(JsonDiffRecord record)
    {
        bool byDefault = record.Status == DiffStatus.Modified && RecordHasChildRecords(record);
        return byDefault ^ recordOverrides.Contains(record.Index);
    }

    // ── The walk ───────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        var newVisible = new List<DiffVisibleRow>(visibleRows.Count);

        var diff = session.Diff;
        if (diff.RecordCount > 0)
        {
            WalkRecordSubtree(0, newVisible);
        }
        else if (!diff.IsComplete && session.Left.Index.TokenCount > 0)
        {
            // Preview: the left document streams into the left pane while both sides index.
            WalkTokenSubtree(RowKind.SubLeft, leftTokenOverrides, 0, 0, -1, DiffStatus.Unchanged, newVisible);
        }

        visibleRows = newVisible;
        lastRebuildCounts = (diff.RecordCount, session.Left.Index.TokenCount);

        rowCache.Clear();
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Emits one record's row (filter permitting) and, when expanded, its child
    /// records or its token-level sub-rows. Returns the next sibling's record index -
    /// <c>RecordCount</c> when this subtree is still streaming, which stops the caller's
    /// sibling loop until the next growth rebuild catches up.</summary>
    private int WalkRecordSubtree(int recordIndex, List<DiffVisibleRow> into)
    {
        var diff = session.Diff;
        var record = diff.GetRecord(recordIndex);
        int next = record.SubtreeEnd < 0 ? diff.RecordCount : record.SubtreeEnd;

        if (changesOnly && record.Status == DiffStatus.Unchanged)
            return next;

        into.Add(new DiffVisibleRow(RowKind.Record, recordIndex, -1, (ushort)record.Depth, -1, record.Status));

        if (!IsRecordExpanded(record))
            return next;

        if (RecordHasChildRecords(record))
        {
            int j = recordIndex + 1;
            int shown = 0;
            while (j < diff.RecordCount && diff.GetRecord(j).ParentRecord == recordIndex)
            {
                if (shown >= ChildCap)
                {
                    into.Add(new DiffVisibleRow(RowKind.Placeholder, recordIndex, -1, (ushort)(record.Depth + 1), -1, record.Status));
                    break;
                }

                j = WalkRecordSubtree(j, into);
                shown++;
            }

            return next;
        }

        // Whole-subtree record: token-level sub-rows from the side that has the content.
        switch (record.Status)
        {
            case DiffStatus.Removed:
                WalkTokenChildren(RowKind.SubLeft, leftTokenOverrides, record.LeftToken, record.Depth, DiffStatus.Removed, into);
                break;
            case DiffStatus.Added:
                WalkTokenChildren(RowKind.SubRight, rightTokenOverrides, record.RightToken, record.Depth, DiffStatus.Added, into);
                break;
            case DiffStatus.Moved:
                // A move's content lives on one side of the row: the stub keeps the left
                // pane (old position), the destination the right pane (new position).
                if (record.IsMoveSource)
                    WalkTokenChildren(RowKind.SubLeft, leftTokenOverrides, record.LeftToken, record.Depth, DiffStatus.Moved, into);
                else if (record.RightToken >= 0)
                    WalkTokenChildren(RowKind.SubRight, rightTokenOverrides, record.RightToken, record.Depth, DiffStatus.Moved, into);
                break;
            default:
                // Unchanged - genuinely present on both sides; rendered from the left
                // document into both panes (identical content by definition).
                if (record.LeftToken >= 0)
                    WalkTokenChildren(RowKind.SubMirror, leftTokenOverrides, record.LeftToken, record.Depth, record.Status, into);
                break;
        }

        return next;
    }

    /// <summary>Adds one token's sub-row and recurses into it when expanded.</summary>
    private void WalkTokenSubtree(RowKind kind, HashSet<int> overrides, int token, int depth, int arrayIndex, DiffStatus tint, List<DiffVisibleRow> into)
    {
        into.Add(new DiffVisibleRow(kind, -1, token, (ushort)depth, arrayIndex, tint));

        var index = SideIndex(kind != RowKind.SubRight);
        var info = index.GetToken(token);
        if (!IsContainer(info.Kind) || !IsSubExpanded(overrides, token, depth))
            return;

        WalkTokenChildren(kind, overrides, token, depth, tint, into);
    }

    /// <summary>Walks one container's direct children (same skip-by-EndIndex pattern as
    /// everywhere else), respecting the display cap and stopping at unindexed regions the
    /// way the preview needs while its side is still streaming in.</summary>
    private void WalkTokenChildren(RowKind kind, HashSet<int> overrides, int containerToken, int depth, DiffStatus tint, List<DiffVisibleRow> into)
    {
        var index = SideIndex(kind != RowKind.SubRight);
        var container = index.GetToken(containerToken);
        bool isArray = container.Kind == JsonTokenKind.StartArray;
        int containerEnd = container.EndIndex;

        int childIndex = containerToken + 1;
        int shown = 0;
        while (true)
        {
            if (containerEnd >= 0 && childIndex >= containerEnd)
                return;

            if (childIndex >= index.TokenCount)
                return; // still indexing (preview); a growth rebuild catches up

            if (shown >= ChildCap)
            {
                into.Add(new DiffVisibleRow(RowKind.Placeholder, -1, containerToken, (ushort)(depth + 1), -1, tint));
                return;
            }

            var child = index.GetToken(childIndex);
            WalkTokenSubtree(kind, overrides, childIndex, depth + 1, isArray ? shown : -1, tint, into);
            shown++;

            if (IsContainer(child.Kind))
            {
                if (child.EndIndex < 0)
                    return;
                childIndex = child.EndIndex + 1;
            }
            else
            {
                childIndex++;
            }
        }
    }

    /// <summary>
    /// Finds the next (direction +1) or previous (-1) actual-change row from
    /// <paramref name="fromPosition"/> (-1 = before the start), wrapping around. A change
    /// row is a record row that is Added, Removed, Moved, or a value-level Modified -
    /// descended containers are path, and sub-rows belong to the block above them, so
    /// both are skipped. O(visible rows); null when the visible list holds no change.
    /// </summary>
    public int? FindNextChange(int fromPosition, int direction)
    {
        int count = visibleRows.Count;
        if (count == 0)
            return null;

        for (int step = 1; step <= count; step++)
        {
            int position = (((fromPosition + step * direction) % count) + count) % count;
            var vrow = visibleRows[position];
            if (vrow.Kind != RowKind.Record)
                continue;

            var record = session.Diff.GetRecord(vrow.RecordIndex);
            bool isChange = record.Status switch
            {
                DiffStatus.Added or DiffStatus.Removed or DiffStatus.Moved => true,
                DiffStatus.Modified => !RecordHasChildRecords(record),
                _ => false
            };

            if (isChange)
                return position;
        }

        return null;
    }

    // ── Row materialization ────────────────────────────────────────────────────────────

    private JsonDiffRow GetRow(int position)
    {
        if (rowCache.TryGetValue(position, out var cached))
            return cached;

        var row = BuildRow(position, visibleRows[position]);
        rowCache.Set(position, row);
        return row;
    }

    private JsonDiffRow BuildRow(int position, DiffVisibleRow vrow)
    {
        switch (vrow.Kind)
        {
            case RowKind.Placeholder:
                return new JsonDiffRow(position, null, null, vrow.Tint, vrow.Depth,
                    hasChildren: false, isExpanded: false, isPlaceholder: true,
                    placeholderText: $"… display limit reached ({ChildCap:N0} rows shown)");

            case RowKind.Record:
                return BuildRecordRow(position, vrow);

            default:
            {
                bool leftSide = vrow.Kind != RowKind.SubRight;
                var overrides = leftSide ? leftTokenOverrides : rightTokenOverrides;
                bool hasChildren = SideHasChildren(leftSide, vrow.Token);
                bool expanded = hasChildren && IsSubExpanded(overrides, vrow.Token, vrow.Depth);
                var factory = leftSide ? leftFactory : rightFactory;
                var jsonRow = factory.BuildRow(position, vrow.Token, vrow.ArrayIndex, schemaNodeId: -1, expanded);

                var left = vrow.Kind != RowKind.SubRight ? jsonRow : null;
                var right = vrow.Kind == RowKind.SubRight ? jsonRow
                    : vrow.Kind == RowKind.SubMirror ? jsonRow : null;

                return new JsonDiffRow(position, left, right, vrow.Tint, vrow.Depth,
                    hasChildren, expanded, isPlaceholder: false);
            }
        }
    }

    private JsonDiffRow BuildRecordRow(int position, DiffVisibleRow vrow)
    {
        var record = session.Diff.GetRecord(vrow.RecordIndex);
        bool hasChildren = RecordHasChildren(record);
        bool expanded = hasChildren && IsRecordExpanded(record);

        // A Moved row's content renders on one side only: the stub keeps the left pane
        // (its old position), the destination the right pane - matching the sub-walks.
        bool showLeft = record.LeftToken >= 0 && (record.Status != DiffStatus.Moved || record.IsMoveSource);
        bool showRight = record.RightToken >= 0 && (record.Status != DiffStatus.Moved || !record.IsMoveSource);

        JsonRow? left = showLeft
            ? leftFactory.BuildRow(position, record.LeftToken, record.LeftArrayIndex, schemaNodeId: -1, expanded)
            : null;
        JsonRow? right = showRight
            ? rightFactory.BuildRow(position, record.RightToken, record.RightArrayIndex, schemaNodeId: -1, expanded)
            : null;

        // A cross-parent Moved renders at BOTH positions: a stub at the source pointing at
        // the destination, the real row at the destination pointing back. Paths are built
        // lazily here, O(depth), only for actually-rendered rows.
        string? moveBadge = null;
        if (record.Status == DiffStatus.Moved)
        {
            if (record.MovePartnerRecord >= 0)
            {
                moveBadge = record.IsMoveSource
                    ? $"moved to {JsonPathBuilder.Build(session.Right.Index, session.Right.File, record.RightToken)} →"
                    : $"↕ moved from {JsonPathBuilder.Build(session.Left.Index, session.Left.File, record.LeftToken)}";
            }
            else
            {
                moveBadge = $"↕ moved from [{record.LeftArrayIndex}]";
            }
        }

        string? note = record.IsAlignmentApproximate ? "alignment approximate" : null;

        // The split behind the two Modified stylings: a descended container is the PATH to
        // a change; an undescended Modified record (leaf, kind mismatch, approximate
        // array) is where the data itself differs.
        bool hasChildRecords = RecordHasChildRecords(record);
        bool isValueChanged = record.Status == DiffStatus.Modified && !hasChildRecords;
        bool isChangedPath = record.Status == DiffStatus.Modified && hasChildRecords;

        return new JsonDiffRow(position, left, right, record.Status, record.Depth,
            hasChildren, expanded, isPlaceholder: false, moveBadge: moveBadge, note: note,
            isValueChanged: isValueChanged, isChangedPath: isChangedPath);
    }

    protected override void DisposeCore()
    {
        growthMonitor?.Dispose();
        growthMonitor = null;
    }
}
