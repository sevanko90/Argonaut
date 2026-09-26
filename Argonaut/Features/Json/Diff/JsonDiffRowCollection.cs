using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Argonaut.Engine.Collections;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Documents;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Expand/collapse-aware flattened projection of a <see cref="JsonDiffIndex"/>'s record
/// log, backing the diff ListBox - one merged list, each row carrying both sides (diff
/// plan stage 5's "one list, not two synced panes"). The walk is a straight iteration
/// over the records, which are already in merged render order, not the JSON tree's cursor,
/// which follows one document rather than an alignment.
///
/// Undescended regions (Unchanged/Added/Removed/Moved subtrees - single records by
/// design) expand into node-level sub-rows read straight off the relevant side's
/// bytes: left for Removed, right for Added, left mirrored into both panes for
/// Unchanged/Moved (their content is semantically identical on both sides; the right
/// pane then shows the left document's key order - a documented v1 simplification).
///
/// Before the first diff record exists (both documents still indexing), the collection
/// renders a live left-document preview so the view is never an empty pane with a
/// spinner; the right pane fills in when the diff starts streaming.
/// </summary>
public sealed class JsonDiffRowCollection : VirtualizingItemsSourceBase
{
    private const int ChildCap = 10_000;

    /// <summary>Ceiling the per-container limit pages up to, matching the JSON view's. Find
    /// pages a container up to here to reach a match past the initial <see cref="ChildCap"/>;
    /// a node beyond even this stays unreachable and is not offered as a stop.</summary>
    private const int MaxDisplayedChildrenPerContainer = 20_000;
    private const int RowCacheCapacity = 1000;

    /// <summary>Bits of a find order key below the owning record's index: the matched row's
    /// offset from the record's own start. 64 GB of one record, and 128M records.</summary>
    private const int OrderKeyOffsetBits = 36;

    // Rebuilding re-walks the visible rows, so growth is folded in at a relaxed cadence.
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
        public DiffVisibleRow(RowKind kind, int recordIndex, TreeNode node, int depth, int arrayIndex, DiffStatus tint)
        {
            Kind = kind;
            RecordIndex = recordIndex;
            Node = node;
            Depth = depth;
            ArrayIndex = arrayIndex;
            Tint = tint;
        }

        public RowKind Kind { get; }
        // Record rows: their own index. Every sub row (SubLeft/SubRight/SubMirror): the
        // enclosing undescended record's index - needed by SubMirror to splice the
        // target-side path (see JsonDiffRow.MirrorRightContainer), and by every kind to
        // let FindNextChange recover "which record owns this visible row" regardless of
        // collapse state. -1 only for a child-cap Placeholder that caps node children
        // directly (no single owning record).
        public int RecordIndex { get; }
        public TreeNode Node { get; }     // sub rows: the node on their side; placeholders: the capped container
        public int Depth { get; }
        public int ArrayIndex { get; }    // sub rows: ordinal among array siblings, or -1
        public DiffStatus Tint { get; }   // sub rows inherit their region's status
    }

    private readonly JsonDiffSession session;
    private readonly JsonDiffDocument left;
    private readonly JsonDiffDocument right;

    // Expand state, override-over-default like the JSON view: records by record index,
    // node-level sub-rows by value start per side (a node appears under at most one
    // region, except a cross-parent move's two ends - which then share state, harmlessly).
    private readonly HashSet<int> recordOverrides = new();
    private readonly HashSet<long> leftNodeOverrides = new();
    private readonly HashSet<long> rightNodeOverrides = new();

    // Raised display limits, over the default ChildCap, for containers find had to page up to
    // reach a match - keyed the same way the override sets are (record index / value start per
    // side). Empty unless find (or a future "show more") needed one, so the common path pays
    // nothing.
    private readonly Dictionary<int, int> recordChildLimit = new();
    private readonly Dictionary<long, int> leftNodeChildLimit = new();
    private readonly Dictionary<long, int> rightNodeChildLimit = new();

    private readonly LruCache<int, JsonDiffRow> rowCache = new(RowCacheCapacity);
    private List<DiffVisibleRow> visibleRows = new();
    private int[]? leftStartOrder;
    private int[]? rightStartOrder;
    private (int, bool) leftStartOrderVersion;
    private (int, bool) rightStartOrderVersion;
    private IndexGrowthMonitor? growthMonitor;
    private (int Records, long LeftBytes) lastRebuildCounts = (-1, -1);
    private bool finalRebuildDone;
    private bool changesOnly;

    public JsonDiffRowCollection(JsonDiffSession session)
    {
        this.session = session;
        this.left = session.LeftDocument;
        this.right = session.RightDocument;

        // Sampled BEFORE the walk, not after: a diff that completes while Rebuild is running
        // would otherwise be seen as "already complete, no monitor needed" by a check made
        // afterwards - and this collection would stay on the pre-diff preview of the left
        // document for the rest of its life, with nothing left to rebuild it. Attaching a
        // monitor to an already-finished task costs one immediate final refresh, which is
        // exactly the refresh that window loses.
        bool diffWasRunning = !session.Diff.AllItemsPublished;

        Rebuild();

        if (diffWasRunning)
        {
            growthMonitor = new IndexGrowthMonitor(GrowthPollInterval, session.Diff.IndexingTask,
                isComplete: () => session.Diff.AllItemsPublished,
                refresh: RefreshIfGrown);
        }
    }

    /// <summary>
    /// See <see cref="IndexGrowthMonitor.FinalRefreshTask"/> - completed already when the diff
    /// was finished before this collection was built, since there is then no monitor and the
    /// constructor's own Rebuild is the final state. Internal: deterministic tests await this
    /// rather than racing the completion refresh.
    /// </summary>
    internal Task FinalRefreshTask => growthMonitor?.FinalRefreshTask ?? Task.CompletedTask;

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

        var counts = (session.Diff.RecordCount, left.Bytes.AvailableLength);
        // The move-reconciliation pass mutates records without growing the log, so the
        // completion refresh must rebuild once even when the counts are unchanged.
        bool completionPass = session.Diff.AllItemsPublished && !finalRebuildDone;
        if (counts == lastRebuildCounts && !completionPass)
            return;

        if (session.Diff.AllItemsPublished)
            finalRebuildDone = true;
        Rebuild();
    }

    protected override int GetCount() => visibleRows.Count;

    protected override object GetItem(int index) => GetRow(index);

    /// <summary>
    /// Toggle expand/collapse of the row at <paramref name="position"/>. Record rows key
    /// their override by record index, node sub-rows by value start on their side;
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
                if (!left.HasChildren(vrow.Node))
                    return;
                Toggle(leftNodeOverrides, vrow.Node.ValueStart);
                break;

            case RowKind.SubRight:
                if (!right.HasChildren(vrow.Node))
                    return;
                Toggle(rightNodeOverrides, vrow.Node.ValueStart);
                break;

            default:
                return;
        }

        Rebuild();
    }

    private static int ChildLimit<TKey>(Dictionary<TKey, int> limits, TKey key) where TKey : notnull
        => limits.TryGetValue(key, out int limit) ? limit : ChildCap;

    /// <summary>Raises <paramref name="key"/>'s display limit far enough to include the child at
    /// <paramref name="childPosition"/>, in whole ChildCap pages and never past the ceiling.
    /// True when the limit actually moved.</summary>
    private static bool PageUpTo<TKey>(Dictionary<TKey, int> limits, TKey key, long childPosition) where TKey : notnull
    {
        int current = ChildLimit(limits, key);
        if (childPosition < current)
            return false;

        int needed = (int)Math.Min(MaxDisplayedChildrenPerContainer, ((childPosition / ChildCap) + 1) * ChildCap);
        if (needed <= current)
            return false;

        limits[key] = needed;
        return true;
    }

    private static void Toggle<TKey>(HashSet<TKey> overrides, TKey key)
    {
        if (!overrides.Remove(key))
            overrides.Add(key);
    }

    private JsonDiffDocument Document(bool leftSide) => leftSide ? left : right;

    /// <summary>Sub-rows and the preview default to "root level expanded, everything below
    /// collapsed", overridden per toggle - the same policy+override shape as the JSON view.</summary>
    private static bool IsSubExpanded(HashSet<long> overrides, long valueStart, int depth)
        => (depth < 1) ^ overrides.Contains(valueStart);

    private bool RecordHasChildRecords(JsonDiffRecord record)
        => record.SubtreeEnd < 0 || record.SubtreeEnd > record.Index + 1;

    private bool RecordHasChildren(JsonDiffRecord record)
    {
        if (RecordHasChildRecords(record))
            return true;
        if (record.IsAlignmentApproximate)
            return false; // not descended, and node sub-walks of both sides would misalign
        if (record.Status is DiffStatus.Added)
            return right.HasChildren(right.NodeAt(record.Right));
        if (record.Left.IsPresent)
            return left.HasChildren(left.NodeAt(record.Left));
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
        else if (!diff.AllItemsPublished && left.Root is { } root)
        {
            // Preview: the left document shows in the left pane while both sides index.
            WalkNodeSubtree(RowKind.SubLeft, root, 0, -1, DiffStatus.Unchanged, newVisible);
        }

        visibleRows = newVisible;
        lastRebuildCounts = (diff.RecordCount, left.Bytes.AvailableLength);

        rowCache.Clear();
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Emits one record's row (filter permitting) and, when expanded, its child
    /// records or its node-level sub-rows. Returns the next sibling's record index -
    /// <c>RecordCount</c> when this subtree is still streaming, which stops the caller's
    /// sibling loop until the next growth rebuild catches up.</summary>
    private int WalkRecordSubtree(int recordIndex, List<DiffVisibleRow> into)
    {
        var diff = session.Diff;
        var record = diff.GetRecord(recordIndex);
        int next = record.SubtreeEnd < 0 ? diff.RecordCount : record.SubtreeEnd;

        if (changesOnly && record.Status == DiffStatus.Unchanged)
            return next;

        into.Add(new DiffVisibleRow(RowKind.Record, recordIndex, default, record.Depth, -1, record.Status));

        if (!IsRecordExpanded(record))
            return next;

        if (RecordHasChildRecords(record))
        {
            int j = recordIndex + 1;
            int shown = 0;
            int cap = ChildLimit(recordChildLimit, recordIndex);
            while (j < diff.RecordCount && diff.GetRecord(j).ParentRecord == recordIndex)
            {
                if (shown >= cap)
                {
                    into.Add(new DiffVisibleRow(RowKind.Placeholder, recordIndex, default, record.Depth + 1, -1, record.Status));
                    break;
                }

                j = WalkRecordSubtree(j, into);
                shown++;
            }

            return next;
        }

        // Whole-subtree record: node-level sub-rows from the side that has the content.
        // recordIndex rides along on every sub-row here too (not just the mirror case below)
        // so FindNextChange can recover "which record does this visible row belong to" from
        // any row, not only Kind.Record ones - see DiffVisibleRow.RecordIndex.
        switch (record.Status)
        {
            case DiffStatus.Removed:
                WalkChildren(RowKind.SubLeft, left.NodeAt(record.Left), record.Depth, DiffStatus.Removed, into, recordIndex);
                break;
            case DiffStatus.Added:
                WalkChildren(RowKind.SubRight, right.NodeAt(record.Right), record.Depth, DiffStatus.Added, into, recordIndex);
                break;
            case DiffStatus.Moved:
                // A move's content lives on one side of the row: the stub keeps the left
                // pane (old position), the destination the right pane (new position).
                if (record.IsMoveSource)
                    WalkChildren(RowKind.SubLeft, left.NodeAt(record.Left), record.Depth, DiffStatus.Moved, into, recordIndex);
                else if (record.Right.IsPresent)
                    WalkChildren(RowKind.SubRight, right.NodeAt(record.Right), record.Depth, DiffStatus.Moved, into, recordIndex);
                break;
            default:
                // Unchanged - genuinely present on both sides; rendered from the left
                // document into both panes (identical content by definition). recordIndex
                // rides along on every sub-row so the target path can later be spliced from
                // this record's own right node (see JsonDiffRow.MirrorRightContainer).
                if (record.Left.IsPresent)
                    WalkChildren(RowKind.SubMirror, left.NodeAt(record.Left), record.Depth, record.Status, into, recordIndex);
                break;
        }

        return next;
    }

    private HashSet<long> Overrides(RowKind kind) => kind == RowKind.SubRight ? rightNodeOverrides : leftNodeOverrides;

    /// <summary>Adds one node's sub-row and recurses into it when expanded.</summary>
    private void WalkNodeSubtree(RowKind kind, TreeNode node, int depth, int arrayIndex, DiffStatus tint, List<DiffVisibleRow> into, int mirrorRecordIndex = -1)
    {
        into.Add(new DiffVisibleRow(kind, mirrorRecordIndex, node, depth, arrayIndex, tint));

        if (!node.IsContainer || !IsSubExpanded(Overrides(kind), node.ValueStart, depth))
            return;

        WalkChildren(kind, node, depth, tint, into, mirrorRecordIndex);
    }

    /// <summary>Walks one container's direct children, respecting the display cap and stopping
    /// where its bytes have not arrived yet, which only the preview meets.</summary>
    private void WalkChildren(RowKind kind, TreeNode container, int depth, DiffStatus tint, List<DiffVisibleRow> into, int mirrorRecordIndex = -1)
    {
        var document = Document(kind != RowKind.SubRight);
        bool isArray = container.FormatKind == (byte)JsonTokenKind.StartArray;
        int shown = 0;
        int cap = ChildLimit(kind == RowKind.SubRight ? rightNodeChildLimit : leftNodeChildLimit, container.ValueStart);
        foreach (var child in document.Children(container))
        {
            if (shown >= cap)
            {
                into.Add(new DiffVisibleRow(RowKind.Placeholder, -1, container, depth + 1, -1, tint));
                return;
            }

            WalkNodeSubtree(kind, child, depth + 1, isArray ? shown : -1, tint, into, mirrorRecordIndex);
            shown++;
        }
    }

    /// <summary>
    /// Finds the next (direction +1) or previous (-1) actual change from
    /// <paramref name="fromPosition"/> (-1 = before the start), wrapping around, and expands
    /// whatever collapsed ancestors stand between it and visibility so the result is always
    /// a valid, showing position. A change is a record that is Added, Removed, Moved, or a
    /// value-level Modified - descended containers are path, not themselves a change.
    ///
    /// Walks <see cref="JsonDiffIndex"/>'s record log directly rather than the visible-row
    /// projection: the log is already in merged render order (same order the fully-expanded
    /// list would have), so stepping through it finds every change regardless of collapse
    /// state - unlike the visible list, which skips anything nested under a collapsed
    /// container entirely. O(record count) to search, plus a rebuild only when an ancestor
    /// actually needed expanding; null when the log holds no change.
    /// </summary>
    public int? FindNextChange(int fromPosition, int direction)
    {
        var diff = session.Diff;
        int count = diff.RecordCount;
        if (count == 0)
            return null;

        int fromRecord = OwnerRecordIndex(fromPosition) ?? -1;

        for (int step = 1; step <= count; step++)
        {
            int recordIndex = (((fromRecord + step * direction) % count) + count) % count;
            var record = diff.GetRecord(recordIndex);
            bool isChange = record.Status switch
            {
                DiffStatus.Added or DiffStatus.Removed or DiffStatus.Moved => true,
                DiffStatus.Modified => !RecordHasChildRecords(record),
                _ => false
            };

            if (isChange)
                return RevealAndLocate(recordIndex);
        }

        return null;
    }

    /// <summary>The record that owns the visible row at <paramref name="position"/> - itself
    /// for a Kind.Record row, its enclosing undescended record for a sub-row - or null when
    /// out of range or unowned (a node-cap Placeholder). Used only as a rough anchor to
    /// resume stepping the record log from the current selection; imprecision here (e.g. an
    /// out-of-date position after an intervening rebuild) only shifts where the cyclic search
    /// starts; it can't cause a change to be missed.</summary>
    private int? OwnerRecordIndex(int position)
    {
        if (position < 0 || position >= visibleRows.Count)
            return null;

        int recordIndex = visibleRows[position].RecordIndex;
        return recordIndex >= 0 ? recordIndex : null;
    }

    /// <summary>Expands every collapsed ancestor of <paramref name="recordIndex"/> - at most
    /// one rebuild, batching every override flip first - then returns that record's own row
    /// position in the (possibly now-different) visible list.</summary>
    private int RevealAndLocate(int recordIndex)
    {
        if (ExpandRecordAncestors(recordIndex))
            Rebuild();

        if (LocateRecordRow(recordIndex) is { } position)
            return position;

        // Every ancestor was just confirmed expanded (or already was), so the record's row
        // must be in the walk - this would only trip if the log itself were inconsistent.
        throw new InvalidOperationException($"Diff record {recordIndex} not found after revealing its ancestors.");
    }

    /// <summary>Flips every collapsed ancestor of <paramref name="recordIndex"/> to expanded,
    /// without rebuilding - the caller batches that. True when anything actually changed.</summary>
    private bool ExpandRecordAncestors(int recordIndex)
    {
        var diff = session.Diff;
        bool changed = false;
        for (int ancestor = diff.GetRecord(recordIndex).ParentRecord; ancestor >= 0; ancestor = diff.GetRecord(ancestor).ParentRecord)
        {
            if (IsRecordExpanded(diff.GetRecord(ancestor)))
                continue;

            Toggle(recordOverrides, ancestor);
            changed = true;
        }

        return changed;
    }

    private int? LocateRecordRow(int recordIndex)
    {
        for (int i = 0; i < visibleRows.Count; i++)
        {
            if (visibleRows[i].Kind == RowKind.Record && visibleRows[i].RecordIndex == recordIndex)
                return i;
        }

        return null;
    }

    // ── Revealing a match (find) ───────────────────────────────────────────────────────

    /// <summary>
    /// Makes the row showing <paramref name="offset"/> of the left (or right) document visible,
    /// expanding whatever records and sub-rows stand in the way, and returns its position.
    /// Backs find, which reveals a match's byte offset on one side and needs the merged list to
    /// show it.
    ///
    /// The row is the deepest node whose bytes hold the offset. Null when it has no row of its
    /// own: the display cap elided it, or - the structural case - it lives inside a region
    /// rendered from the OTHER side (<see cref="RowOrderKey"/> keeps find from stopping on those).
    /// </summary>
    public int? EnsureVisible(bool leftSide, long offset)
    {
        int owner = FindRecordCovering(leftSide, offset);
        if (owner < 0)
            return null;

        var record = session.Diff.GetRecord(owner);
        var ownerNode = leftSide ? record.Left : record.Right;
        var document = Document(leftSide);
        var chain = document.Locate(offset);
        int ownerAt = IndexOf(chain, ownerNode.ValueStart);
        if (ownerAt < 0)
            return null;

        long target = chain[^1].Node.ValueStart;
        bool changed = ExpandRecordAncestors(owner) | PageUpToRecord(owner);

        // The record's own row IS the match's row - nothing below it needs opening.
        if (target != ownerNode.ValueStart && NodeSubWalkIsLeft(record) == leftSide && !RecordHasChildRecords(record))
        {
            if (!IsRecordExpanded(record))
            {
                Toggle(recordOverrides, owner);
                changed = true;
            }

            var overrides = leftSide ? leftNodeOverrides : rightNodeOverrides;
            var limits = leftSide ? leftNodeChildLimit : rightNodeChildLimit;

            // Sub-rows sit at merged depth >= 1, where IsSubExpanded's default is collapsed -
            // so membership of the override set is exactly "expanded" for them, and adding is
            // exactly "expand". Opens each node strictly between the record and the target, and
            // pages each container's display limit far enough to include the child on the way
            // down - an expanded container still hides a child past its cap.
            for (int i = ownerAt + 1; i < chain.Count; i++)
            {
                changed |= PageUpTo(limits, chain[i - 1].Node.ValueStart, chain[i].Ordinal);
                if (i < chain.Count - 1)
                    changed |= overrides.Add(chain[i].Node.ValueStart);
            }
        }

        if (changed)
            Rebuild();

        for (int i = 0; i < visibleRows.Count; i++)
        {
            var vrow = visibleRows[i];
            if (vrow.Kind == RowKind.Record)
            {
                if (vrow.RecordIndex == owner && target == ownerNode.ValueStart)
                    return i;
                continue;
            }

            if (vrow.Kind != RowKind.Placeholder && vrow.Node.ValueStart == target && (vrow.Kind != RowKind.SubRight) == leftSide)
                return i;
        }

        // Deliberately NOT falling back to the record's own row. That row sits above the match,
        // so selecting it sends find-next backwards up the tree - the very thing that made
        // stepping through a large diff feel broken. RowOrderKey screens out the matches that
        // genuinely cannot be drawn, so reaching here means "nothing to move to"; leaving the
        // selection alone is the honest answer.
        return null;
    }

    private static int IndexOf(IReadOnlyList<TreeRow> chain, long valueStart)
    {
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Node.ValueStart == valueStart)
                return i;
        }

        return -1;
    }

    /// <summary>Raises the record-child display limit on every ancestor of
    /// <paramref name="recordIndex"/> far enough to include the child leading to it.</summary>
    private bool PageUpToRecord(int recordIndex)
    {
        var diff = session.Diff;
        bool changed = false;

        for (int child = recordIndex, parent = diff.GetRecord(recordIndex).ParentRecord;
             parent >= 0;
             child = parent, parent = diff.GetRecord(parent).ParentRecord)
        {
            changed |= PageUpTo(recordChildLimit, parent, RecordChildPosition(parent, child));
        }

        return changed;
    }

    /// <summary>Ordinal of <paramref name="child"/> among its parent record's direct children,
    /// walking the same subtree-skipping chain the render walk uses.</summary>
    private int RecordChildPosition(int parent, int child)
    {
        var diff = session.Diff;
        int position = 0;
        int j = parent + 1;
        while (j < diff.RecordCount && j != child && diff.GetRecord(j).ParentRecord == parent)
        {
            var record = diff.GetRecord(j);
            j = record.SubtreeEnd < 0 ? diff.RecordCount : record.SubtreeEnd;
            position++;
        }

        return position;
    }

    /// <summary>
    /// Where the match at <paramref name="offset"/> falls in the merged display order, as a
    /// sortable key - what lets one find bar interleave matches from both documents into a
    /// single sequence (see <see cref="Argonaut.Ui.Find.ISearchNavigator.OrderKey"/>). Keyed on
    /// the owning RECORD, not on a visible row position, so it stays put as the user expands and
    /// collapses; long.MaxValue for a match no record covers yet, which parks it at the end
    /// rather than at the start. Matches in one row share a key, which makes them one stop.
    ///
    /// Null when this side's bytes are not what that part of the tree renders, so find skips
    /// the match entirely: an undescended subtree is walked from ONE document into both panes
    /// (the left one for Unchanged, whichever end a Moved record sits at), so the other
    /// document's bytes there are on nobody's screen. They are not lost - identical content is
    /// being displayed, found via the side that is actually rendered - and stopping on them
    /// would highlight nothing while appearing to jump backwards up the tree.
    /// </summary>
    public long? RowOrderKey(bool leftSide, long offset)
    {
        int owner = FindRecordCovering(leftSide, offset);
        if (owner < 0)
            return long.MaxValue;

        var record = session.Diff.GetRecord(owner);
        var ownerNode = leftSide ? record.Left : record.Right;
        var chain = Document(leftSide).Locate(offset);
        long target = chain.Count > 0 ? chain[^1].Node.ValueStart : ownerNode.ValueStart;

        // The record's own row, which fills both panes. Deliberately keyed WITHOUT which side
        // the match came from: find stops once per row, so the same key for both panes is what
        // collapses "the term is in the source and the target of this row" into one stop.
        // Suppressed only where that pane is not drawn at all, which is a Moved record - it
        // renders one end only. Sorts ahead of everything below it.
        if (target == ownerNode.ValueStart || IndexOf(chain, ownerNode.ValueStart) < 0)
        {
            bool rendered = leftSide ? RecordRowShowsLeft(record) : RecordRowShowsRight(record);
            return rendered ? (long)owner << OrderKeyOffsetBits : null;
        }

        // Below the record, one document supplies both panes - see NodeSubWalkIsLeft.
        if (NodeSubWalkIsLeft(record) != leftSide)
            return null;

        // Nothing below this record is drawn from nodes at all: a descended record's children
        // are its child RECORDS, and an approximate array is deliberately never opened. Either
        // way the match has no row, so it is not a stop - without this the reveal would fall
        // back to the record's own row, which sits above the stop just visited and reads as
        // find-next running backwards.
        if (RecordHasChildRecords(record) || record.IsAlignmentApproximate)
            return null;

        // Only one side contributes rows down here, so ordering the tail by offset is document
        // order for it, and keying by the row's own start makes one key per row. Offset past
        // the record-row key above.
        long below = Math.Min(target - ownerNode.RowStart + 1, (1L << OrderKeyOffsetBits) - 1);
        return ((long)owner << OrderKeyOffsetBits) | below;
    }

    // Which panes a record's OWN row fills - the predicate form of BuildRecordRow's showLeft/
    // showRight, which is a different question from which document its sub-rows come from.
    private static bool RecordRowShowsLeft(JsonDiffRecord record)
        => record.Left.IsPresent && (record.Status != DiffStatus.Moved || record.IsMoveSource);

    private static bool RecordRowShowsRight(JsonDiffRecord record)
        => record.Right.IsPresent && (record.Status != DiffStatus.Moved || !record.IsMoveSource);

    /// <summary>Which document an undescended record's node sub-rows are walked from - see the
    /// switch in <see cref="WalkRecordSubtree"/>, of which this is the predicate form.</summary>
    private static bool NodeSubWalkIsLeft(JsonDiffRecord record) => record.Status switch
    {
        DiffStatus.Added => false,
        DiffStatus.Removed => true,
        DiffStatus.Moved => record.IsMoveSource,
        _ => true, // Unchanged/undescended: mirrored from the left document into both panes.
    };

    /// <summary>
    /// The deepest record whose subtree on <paramref name="leftSide"/> holds
    /// <paramref name="offset"/>, or -1. A binary search over the records ordered by where they
    /// start, then up the parent chain - no offset-to-record map, which at multi-GB scale would
    /// cost as much as the diff itself.
    /// </summary>
    private int FindRecordCovering(bool leftSide, long offset)
    {
        var diff = session.Diff;
        var order = StartOrder(leftSide);
        if (order.Length == 0)
            return -1;

        // Greatest record whose subtree STARTS at or before the offset. In merged (DFS) order
        // that is the deepest candidate; if its subtree ends before the offset, the offset sits
        // in a gap and the covering record is one of its ancestors.
        int lo = 0, hi = order.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Side(diff.GetRecord(order[mid]), leftSide).RowStart <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (int candidate = found >= 0 ? order[found] : -1; candidate >= 0; candidate = diff.GetRecord(candidate).ParentRecord)
        {
            if (RecordCovers(diff.GetRecord(candidate), leftSide, offset))
                return candidate;
        }

        return -1;
    }

    private static JsonDiffNode Side(JsonDiffRecord record, bool leftSide) => leftSide ? record.Left : record.Right;

    /// <summary>
    /// Record indices that draw this side, ordered by where their node starts, so
    /// <see cref="FindRecordCovering"/> is a binary search rather than a walk down the record
    /// tree scanning siblings from the start of each level. Both records of a cross-parent move
    /// hold both sides' nodes, but each draws one - the stub the left, the destination the right -
    /// so the other is left out, or a match there could resolve to the end that does not show it.
    ///
    /// Built lazily, only if find is actually used, and again if the log has changed since - it
    /// grows while the diff runs, and the move pass rewrites records when it ends. One int per
    /// record per side; the offsets themselves are read back through GetRecord rather than
    /// copied, to keep this a fraction of what the log it indexes already costs.
    /// </summary>
    private int[] StartOrder(bool leftSide)
    {
        var diff = session.Diff;
        var version = (diff.RecordCount, diff.AllItemsPublished);
        ref int[]? cached = ref leftSide ? ref leftStartOrder : ref rightStartOrder;
        ref (int, bool) cachedVersion = ref leftSide ? ref leftStartOrderVersion : ref rightStartOrderVersion;
        if (cached is not null && cachedVersion == version)
            return cached;

        var order = new List<int>(version.RecordCount);
        for (int i = 0; i < version.RecordCount; i++)
        {
            var record = diff.GetRecord(i);
            if (Side(record, leftSide).IsPresent && (record.Status != DiffStatus.Moved || record.IsMoveSource == leftSide))
                order.Add(i);
        }

        var array = order.ToArray();
        Array.Sort(array, (a, b) => Side(diff.GetRecord(a), leftSide).RowStart.CompareTo(Side(diff.GetRecord(b), leftSide).RowStart));
        cached = array;
        cachedVersion = version;
        return array;
    }

    /// <summary>Whether <paramref name="offset"/> lies within this record's node on the given
    /// side - its name included. A container still arriving covers everything after it, which
    /// is what keeps a reveal working against a partially-indexed file.</summary>
    private bool RecordCovers(JsonDiffRecord record, bool leftSide, long offset)
    {
        var at = Side(record, leftSide);
        if (!at.IsPresent || offset < at.RowStart)
            return false;

        var document = Document(leftSide);
        return offset < document.End(document.NodeAt(at));
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
                var document = Document(leftSide);
                bool hasChildren = document.HasChildren(vrow.Node);
                bool expanded = hasChildren && IsSubExpanded(Overrides(vrow.Kind), vrow.Node.ValueStart, vrow.Depth);
                var jsonRow = document.BuildRow(position, vrow.Node, vrow.ArrayIndex, vrow.Depth, expanded);

                var leftRow = vrow.Kind != RowKind.SubRight ? jsonRow : null;
                var rightRow = vrow.Kind == RowKind.SubRight ? jsonRow
                    : vrow.Kind == RowKind.SubMirror ? jsonRow : null;

                long? mirrorLeftContainer = null;
                long? mirrorRightContainer = null;
                if (vrow.Kind == RowKind.SubMirror && vrow.RecordIndex >= 0)
                {
                    var mirrorRecord = session.Diff.GetRecord(vrow.RecordIndex);
                    mirrorLeftContainer = mirrorRecord.Left.ValueStart;
                    mirrorRightContainer = mirrorRecord.Right.IsPresent ? mirrorRecord.Right.ValueStart : null;
                }

                return new JsonDiffRow(position, leftRow, rightRow, vrow.Tint, vrow.Depth,
                    hasChildren, expanded, isPlaceholder: false,
                    mirrorLeftContainer: mirrorLeftContainer, mirrorRightContainer: mirrorRightContainer);
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
        JsonRow? leftRow = RecordRowShowsLeft(record)
            ? left.BuildRow(position, left.NodeAt(record.Left), record.LeftArrayIndex, record.Depth, expanded)
            : null;
        JsonRow? rightRow = RecordRowShowsRight(record)
            ? right.BuildRow(position, right.NodeAt(record.Right), record.RightArrayIndex, record.Depth, expanded)
            : null;

        // A cross-parent Moved renders at BOTH positions: a stub at the source pointing at
        // the destination, the real row at the destination pointing back. Paths are built
        // lazily here, only for actually-rendered rows.
        string? moveBadge = null;
        if (record.Status == DiffStatus.Moved)
        {
            if (record.MovePartnerRecord >= 0)
            {
                moveBadge = record.IsMoveSource
                    ? $"moved to {right.Path(record.Right.ValueStart)} →"
                    : $"↕ moved from {left.Path(record.Left.ValueStart)}";
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

        return new JsonDiffRow(position, leftRow, rightRow, record.Status, record.Depth,
            hasChildren, expanded, isPlaceholder: false, moveBadge: moveBadge, note: note,
            isValueChanged: isValueChanged, isChangedPath: isChangedPath);
    }

    protected override void DisposeCore()
    {
        growthMonitor?.Dispose();
        growthMonitor = null;
    }
}
