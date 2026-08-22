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

    /// <summary>Ceiling the per-container limit pages up to, matching the JSON view's. Find
    /// pages a container up to here to reach a match past the initial <see cref="ChildCap"/>;
    /// a token beyond even this stays unreachable and is not offered as a stop.</summary>
    private const int MaxDisplayedChildrenPerContainer = 20_000;
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
        // Record rows: their own index. Every sub row (SubLeft/SubRight/SubMirror): the
        // enclosing undescended record's index - needed by SubMirror to splice the
        // target-side path (see JsonDiffRow.MirrorRightContainerToken), and by every kind to
        // let FindNextChange recover "which record owns this visible row" regardless of
        // collapse state. -1 only for a child-cap Placeholder that caps token children
        // directly (no single owning record).
        public int RecordIndex { get; }
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

    // Raised display limits, over the default ChildCap, for containers find had to page up to
    // reach a match - keyed the same way the override sets are (record index / token per side).
    // Empty unless find (or a future "show more") needed one, so the common path pays nothing.
    private readonly Dictionary<int, int> recordChildLimit = new();
    private readonly Dictionary<int, int> leftTokenChildLimit = new();
    private readonly Dictionary<int, int> rightTokenChildLimit = new();

    private readonly LruCache<int, JsonDiffRow> rowCache = new(RowCacheCapacity);
    private List<DiffVisibleRow> visibleRows = new();
    private int[]? leftStartOrder;
    private int[]? rightStartOrder;
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

    private static int ChildLimit(Dictionary<int, int> limits, int key)
        => limits.TryGetValue(key, out int limit) ? limit : ChildCap;

    /// <summary>Raises <paramref name="key"/>'s display limit far enough to include the child at
    /// <paramref name="childPosition"/>, in whole ChildCap pages and never past the ceiling.
    /// True when the limit actually moved. Mirrors JsonVisibleRowCollection.EnsureVisible's
    /// paging, so "find reached it" and "the user clicked show more enough times" agree.</summary>
    private static bool PageUpTo(Dictionary<int, int> limits, int key, int childPosition)
    {
        int current = ChildLimit(limits, key);
        if (childPosition < current)
            return false;

        int needed = Math.Min(MaxDisplayedChildrenPerContainer, ((childPosition / ChildCap) + 1) * ChildCap);
        if (needed <= current)
            return false;

        limits[key] = needed;
        return true;
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
            int cap = ChildLimit(recordChildLimit, recordIndex);
            while (j < diff.RecordCount && diff.GetRecord(j).ParentRecord == recordIndex)
            {
                if (shown >= cap)
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
        // recordIndex rides along on every sub-row here too (not just the mirror case below)
        // so FindNextChange can recover "which record does this visible row belong to" from
        // any row, not only Kind.Record ones - see DiffVisibleRow.RecordIndex.
        switch (record.Status)
        {
            case DiffStatus.Removed:
                WalkTokenChildren(RowKind.SubLeft, leftTokenOverrides, record.LeftToken, record.Depth, DiffStatus.Removed, into, recordIndex);
                break;
            case DiffStatus.Added:
                WalkTokenChildren(RowKind.SubRight, rightTokenOverrides, record.RightToken, record.Depth, DiffStatus.Added, into, recordIndex);
                break;
            case DiffStatus.Moved:
                // A move's content lives on one side of the row: the stub keeps the left
                // pane (old position), the destination the right pane (new position).
                if (record.IsMoveSource)
                    WalkTokenChildren(RowKind.SubLeft, leftTokenOverrides, record.LeftToken, record.Depth, DiffStatus.Moved, into, recordIndex);
                else if (record.RightToken >= 0)
                    WalkTokenChildren(RowKind.SubRight, rightTokenOverrides, record.RightToken, record.Depth, DiffStatus.Moved, into, recordIndex);
                break;
            default:
                // Unchanged - genuinely present on both sides; rendered from the left
                // document into both panes (identical content by definition). recordIndex
                // rides along on every sub-row so the target path can later be spliced from
                // this record's own RightToken (see JsonDiffRow.MirrorRightContainerToken).
                if (record.LeftToken >= 0)
                    WalkTokenChildren(RowKind.SubMirror, leftTokenOverrides, record.LeftToken, record.Depth, record.Status, into, recordIndex);
                break;
        }

        return next;
    }

    /// <summary>Adds one token's sub-row and recurses into it when expanded.</summary>
    private void WalkTokenSubtree(RowKind kind, HashSet<int> overrides, int token, int depth, int arrayIndex, DiffStatus tint, List<DiffVisibleRow> into, int mirrorRecordIndex = -1)
    {
        into.Add(new DiffVisibleRow(kind, mirrorRecordIndex, token, (ushort)depth, arrayIndex, tint));

        var index = SideIndex(kind != RowKind.SubRight);
        var info = index.GetToken(token);
        if (!IsContainer(info.Kind) || !IsSubExpanded(overrides, token, depth))
            return;

        WalkTokenChildren(kind, overrides, token, depth, tint, into, mirrorRecordIndex);
    }

    /// <summary>Walks one container's direct children (same skip-by-EndIndex pattern as
    /// everywhere else), respecting the display cap and stopping at unindexed regions the
    /// way the preview needs while its side is still streaming in.</summary>
    private void WalkTokenChildren(RowKind kind, HashSet<int> overrides, int containerToken, int depth, DiffStatus tint, List<DiffVisibleRow> into, int mirrorRecordIndex = -1)
    {
        var index = SideIndex(kind != RowKind.SubRight);
        var container = index.GetToken(containerToken);
        bool isArray = container.Kind == JsonTokenKind.StartArray;
        int containerEnd = container.EndIndex;

        int childIndex = containerToken + 1;
        int shown = 0;
        int cap = ChildLimit(kind == RowKind.SubRight ? rightTokenChildLimit : leftTokenChildLimit, containerToken);
        while (true)
        {
            if (containerEnd >= 0 && childIndex >= containerEnd)
                return;

            if (childIndex >= index.TokenCount)
                return; // still indexing (preview); a growth rebuild catches up

            if (shown >= cap)
            {
                into.Add(new DiffVisibleRow(RowKind.Placeholder, -1, containerToken, (ushort)(depth + 1), -1, tint));
                return;
            }

            var child = index.GetToken(childIndex);
            WalkTokenSubtree(kind, overrides, childIndex, depth + 1, isArray ? shown : -1, tint, into, mirrorRecordIndex);
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
    /// out of range or unowned (a token-cap Placeholder). Used only as a rough anchor to
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

    // ── Revealing a token (find) ───────────────────────────────────────────────────────

    /// <summary>
    /// Makes the row showing <paramref name="token"/> of the left (or right) document visible,
    /// expanding whatever records and sub-rows stand in the way, and returns its position.
    /// Backs find, which resolves a match's byte offset to a token on one side and needs the
    /// merged list to show it - the diff's counterpart to JsonVisibleRowCollection.EnsureVisible.
    ///
    /// Falls back to the enclosing record's own row (rather than failing) whenever the exact
    /// token has no row of its own: the display cap elided it, or - the structural case - it
    /// lives inside a region rendered from the OTHER side. An undescended Unchanged subtree is
    /// walked off the left document into both panes, so a right-document token in there is
    /// never a row; its content is on screen, mirrored, at the record it belongs to.
    ///
    /// Null only when the token belongs to no record at all (nothing indexed yet).
    /// </summary>
    public int? EnsureVisible(bool leftSide, int token)
    {
        int owner = FindRecordCovering(leftSide, token);
        if (owner < 0)
            return null;

        var record = session.Diff.GetRecord(owner);
        int ownerToken = leftSide ? record.LeftToken : record.RightToken;

        bool changed = ExpandRecordAncestors(owner) | PageUpToRecord(owner);

        // The record's own row IS the token's row - nothing below it needs opening.
        if (token != ownerToken && TokenSubWalkIsLeft(record) == leftSide && !RecordHasChildRecords(record))
        {
            if (!IsRecordExpanded(record))
            {
                Toggle(recordOverrides, owner);
                changed = true;
            }

            var overrides = leftSide ? leftTokenOverrides : rightTokenOverrides;
            var limits = leftSide ? leftTokenChildLimit : rightTokenChildLimit;
            var index = SideIndex(leftSide);

            // Sub-rows sit at merged depth >= 1, where IsSubExpanded's default is collapsed -
            // so membership of the override set is exactly "expanded" for them, and adding is
            // exactly "expand". Walks strictly between the token and the record's own token,
            // opening each ancestor AND paging its display limit far enough to include the
            // child on the way down - an expanded ancestor still hides a child past its cap.
            for (int child = token, ancestor = index.GetToken(token).ParentIndex;
                 ancestor >= 0;
                 child = ancestor, ancestor = index.GetToken(ancestor).ParentIndex)
            {
                changed |= PageUpTo(limits, ancestor, SideChildPosition(leftSide, ancestor, child));

                if (ancestor == ownerToken)
                    break;

                changed |= overrides.Add(ancestor);
            }
        }

        if (changed)
            Rebuild();

        for (int i = 0; i < visibleRows.Count; i++)
        {
            var vrow = visibleRows[i];
            if (vrow.Kind == RowKind.Record)
            {
                if (vrow.RecordIndex == owner && token == ownerToken)
                    return i;
                continue;
            }

            if (vrow.Kind != RowKind.Placeholder && vrow.Token == token && (vrow.Kind != RowKind.SubRight) == leftSide)
                return i;
        }

        // Deliberately NOT falling back to the record's own row. That row sits above the token,
        // so selecting it sends find-next backwards up the tree - the very thing that made
        // stepping through a large diff feel broken. RowOrderKey screens out the tokens that
        // genuinely cannot be drawn, so reaching here means "nothing to move to"; leaving the
        // selection alone is the honest answer.
        return null;
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

    /// <summary>Ordinal of <paramref name="child"/> among its container's direct token children
    /// - the same EndIndex-skipping walk WalkTokenChildren does when it renders them.</summary>
    private int SideChildPosition(bool leftSide, int containerToken, int child)
    {
        var index = SideIndex(leftSide);
        int position = 0;
        int childIndex = containerToken + 1;

        while (childIndex < index.TokenCount && childIndex != child)
        {
            var token = index.GetToken(childIndex);
            if (IsContainer(token.Kind))
            {
                if (token.EndIndex < 0)
                    break;
                childIndex = token.EndIndex + 1;
            }
            else
            {
                childIndex++;
            }

            position++;
        }

        return position;
    }

    /// <summary>
    /// Where <paramref name="token"/> falls in the merged display order, as a sortable key -
    /// what lets one find bar interleave matches from both documents into a single sequence
    /// (see <see cref="Search.ISearchNavigator.OrderKey"/>). Keyed on the owning RECORD, not on
    /// a visible row position, so it stays put as the user expands and collapses; long.MaxValue
    /// for a token no record covers yet, which parks it at the end rather than at the start.
    ///
    /// Null when this side's bytes are not what that part of the tree renders, so find skips
    /// the match entirely: an undescended subtree is walked from ONE document into both panes
    /// (the left one for Unchanged, whichever end a Moved record sits at), so the other
    /// document's bytes there are on nobody's screen. They are not lost - identical content is
    /// being displayed, found via the side that is actually rendered - and stopping on them
    /// would highlight nothing while appearing to jump backwards up the tree.
    /// </summary>
    public long? RowOrderKey(bool leftSide, int token)
    {
        int owner = FindRecordCovering(leftSide, token);
        if (owner < 0)
            return long.MaxValue;

        var record = session.Diff.GetRecord(owner);
        int ownerToken = leftSide ? record.LeftToken : record.RightToken;

        // The record's own row. It shows each side in its own pane, so a match in either is
        // genuinely on screen and worth a stop - except on a Moved record, which renders one
        // end only. Sorted ahead of everything below it, left pane first.
        if (token == ownerToken)
        {
            bool rendered = leftSide ? RecordRowShowsLeft(record) : RecordRowShowsRight(record);
            return rendered ? ((long)owner << 32) | (uint)(leftSide ? 0 : 1) : null;
        }

        // Below the record, one document supplies both panes - see TokenSubWalkIsLeft.
        if (TokenSubWalkIsLeft(record) != leftSide)
            return null;

        // Nothing below this record is drawn from tokens at all: a descended record's children
        // are its child RECORDS, and an approximate array is deliberately never opened. Either
        // way the token has no row, so it is not a stop - without this the reveal would fall
        // back to the record's own row, which sits above the stop just visited and reads as
        // find-next running backwards.
        if (RecordHasChildRecords(record) || record.IsAlignmentApproximate)
            return null;

        // Only one side contributes rows down here, so ordering the tail by raw token index is
        // document order for it. Offset past the two record-row keys above.
        return ((long)owner << 32) | ((uint)token + 2);
    }

    // Which panes a record's OWN row fills - the predicate form of BuildRecordRow's showLeft/
    // showRight, which is a different question from which document its sub-rows come from.
    private static bool RecordRowShowsLeft(JsonDiffRecord record)
        => record.LeftToken >= 0 && (record.Status != DiffStatus.Moved || record.IsMoveSource);

    private static bool RecordRowShowsRight(JsonDiffRecord record)
        => record.RightToken >= 0 && (record.Status != DiffStatus.Moved || !record.IsMoveSource);

    /// <summary>Which document an undescended record's token sub-rows are walked from - see the
    /// switch in <see cref="WalkRecordSubtree"/>, of which this is the predicate form.</summary>
    private static bool TokenSubWalkIsLeft(JsonDiffRecord record) => record.Status switch
    {
        DiffStatus.Added => false,
        DiffStatus.Removed => true,
        DiffStatus.Moved => record.IsMoveSource,
        _ => true, // Unchanged/undescended: mirrored from the left document into both panes.
    };

    /// <summary>
    /// The deepest record whose subtree on <paramref name="leftSide"/> contains
    /// <paramref name="token"/>, or -1. Descends the record tree the same way the walk does
    /// (children scanned by ParentRecord, whole subtrees skipped via SubtreeEnd), so it costs
    /// O(depth x siblings-per-level) rather than a pass over the whole log - and needs no
    /// token-to-record map, which at multi-GB scale would cost as much as the diff itself.
    /// </summary>
    private int FindRecordCovering(bool leftSide, int token)
    {
        var diff = session.Diff;
        var order = StartOrder(leftSide);
        if (order.Length == 0)
            return -1;

        // Greatest record whose subtree STARTS at or before the token. In merged (DFS) order
        // that is the deepest candidate; if its subtree ends before the token, the token sits
        // in a gap and the covering record is one of its ancestors.
        int lo = 0, hi = order.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (StartToken(diff.GetRecord(order[mid]), leftSide) <= token)
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
            if (RecordCovers(diff.GetRecord(candidate), leftSide, token))
                return candidate;
        }

        return -1;
    }

    private static int StartToken(JsonDiffRecord record, bool leftSide)
        => leftSide ? record.LeftToken : record.RightToken;

    /// <summary>
    /// Record indices that touch this side, ordered by the token their subtree starts at, so
    /// <see cref="FindRecordCovering"/> is a binary search rather than a walk down the record
    /// tree scanning siblings from the start of each level. That scan was O(records before the
    /// match), which made every find press on a large file cost more than the last.
    ///
    /// Built once, lazily, and only if find is actually used - the record log is append-only
    /// and final by the time a diff is searchable, so it never needs invalidating. One int per
    /// record per side; the tokens themselves are read back through GetRecord rather than
    /// copied, to keep this a fraction of what the log it indexes already costs.
    /// </summary>
    private int[] StartOrder(bool leftSide)
    {
        ref int[]? cached = ref leftSide ? ref leftStartOrder : ref rightStartOrder;
        if (cached is not null)
            return cached;

        var diff = session.Diff;
        int count = diff.RecordCount;
        var order = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            if (StartToken(diff.GetRecord(i), leftSide) >= 0)
                order.Add(i);
        }

        var array = order.ToArray();
        Array.Sort(array, (a, b) => StartToken(diff.GetRecord(a), leftSide).CompareTo(StartToken(diff.GetRecord(b), leftSide)));
        cached = array;
        return array;
    }

    /// <summary>Whether <paramref name="token"/> lies within this record's subtree on the given
    /// side. A container still streaming (EndIndex &lt; 0) is treated as covering everything
    /// after it, which is what keeps a reveal working against a partially-indexed file.</summary>
    private bool RecordCovers(JsonDiffRecord record, bool leftSide, int token)
    {
        int start = leftSide ? record.LeftToken : record.RightToken;
        if (start < 0 || token < start)
            return false;

        var info = SideIndex(leftSide).GetToken(start);
        if (!IsContainer(info.Kind))
            return token == start;

        return info.EndIndex < 0 || token <= info.EndIndex;
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

                int? mirrorLeftContainer = null;
                int? mirrorRightContainer = null;
                if (vrow.Kind == RowKind.SubMirror && vrow.RecordIndex >= 0)
                {
                    var mirrorRecord = session.Diff.GetRecord(vrow.RecordIndex);
                    mirrorLeftContainer = mirrorRecord.LeftToken;
                    mirrorRightContainer = mirrorRecord.RightToken >= 0 ? mirrorRecord.RightToken : null;
                }

                return new JsonDiffRow(position, left, right, vrow.Tint, vrow.Depth,
                    hasChildren, expanded, isPlaceholder: false,
                    mirrorLeftContainerToken: mirrorLeftContainer, mirrorRightContainerToken: mirrorRightContainer);
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
