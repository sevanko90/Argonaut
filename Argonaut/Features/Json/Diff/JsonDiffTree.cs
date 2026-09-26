using System;
using System.Collections.Generic;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Diff;

/// <summary>How a region's rows fill the two panes.</summary>
internal enum RegionPanes : byte
{
    LeftOnly,
    RightOnly,

    /// <summary>Unchanged pairs: drawn from the left document into both panes.</summary>
    Mirror,
}

/// <summary>
/// A span of siblings on one side that a record shows row by row: a node's children, a run's
/// pairs, one side of a range. Walked by a <see cref="TreeCursor"/> on that side.
/// </summary>
/// <param name="IsLeft">Which document the region walks.</param>
/// <param name="ParentDocDepth">Document depth of the container holding the span; -1 for the
/// document level. Its children are one deeper.</param>
/// <param name="Base">The offset a row's key is measured from: the container's value start, or 0
/// at the document level.</param>
/// <param name="FirstOrdinal">The span's first child.</param>
/// <param name="Count">How many children the span has; <c>long.MaxValue</c> for all of them.</param>
/// <param name="FirstRowStart">Where the span's first child's row starts.</param>
/// <param name="LastByte">A byte of the span's last child's closing - its last byte, or the
/// container's closing bracket when the span is all its children.</param>
/// <param name="MergedDepth">The merged-tree depth of the span's children.</param>
/// <param name="Panes">How the rows are drawn.</param>
/// <param name="RightFirstOrdinal">For a run, the right ordinal of its first pair.</param>
internal readonly record struct DiffRegion(
    bool IsLeft,
    int ParentDocDepth,
    long Base,
    long FirstOrdinal,
    long Count,
    long FirstRowStart,
    long LastByte,
    int MergedDepth,
    RegionPanes Panes,
    long RightFirstOrdinal)
{
    public int ChildDocDepth => ParentDocDepth + 1;

    public bool Holds(long ordinal) => ordinal >= FirstOrdinal && (Count == long.MaxValue || ordinal - FirstOrdinal < Count);
}

/// <summary>
/// The merged diff tree as <see cref="TreeSurface"/> draws it: rows walked by
/// <see cref="JsonDiffCursor"/> over the diff's record log, drawn two panes wide by
/// <see cref="JsonDiffPainter"/>. Owns what the rows' expansion is, whether unchanged rows show,
/// and the lookups from a left offset or a find match to a row's key.
///
/// <b>Keys.</b> A row's <see cref="TreeRow.Start"/> - also its node's value start, which is all its
/// node is - is <c>record &lt;&lt; 39</c> for a record's own row, plus
/// <c>region &lt;&lt; 38 | (row start - region base + 1)</c> for a row in one of its regions. Keys
/// follow merged order, so find sorts matches by the key of the row drawing them.
///
/// Single-threaded, on the UI thread, like the surface; the log it reads may still be growing.
/// </summary>
public sealed class JsonDiffTree : ITreeRowSource
{
    internal const int RecordShift = 39;
    internal const int RegionShift = 38;
    internal const long LocalMask = (1L << RegionShift) - 1;

    private readonly HashSet<int> recordOverrides = new();
    private bool changesOnly;

    // Ordered by where each record's drawn node starts on that side, for find; built only once
    // find is used, and again when the log has changed since.
    private int[]? leftStartOrder;
    private int[]? rightStartOrder;
    private (int, bool) leftStartOrderVersion;
    private (int, bool) rightStartOrderVersion;

    public JsonDiffTree(JsonDiffSession session)
    {
        Session = session;
        Painter = new JsonDiffPainter(this);
    }

    internal JsonDiffSession Session { get; }

    public JsonDiffIndex Diff => Session.Diff;

    public JsonDiffDocument Left => Session.LeftDocument;

    public JsonDiffDocument Right => Session.RightDocument;

    public ITreeRowPainter Painter { get; }

    public IReadOnlyList<ITreeGutter> Gutters { get; } = Array.Empty<ITreeGutter>();

    /// <summary>Node-level expansion inside regions, per side: collapsed unless toggled.</summary>
    internal TreeExpandState LeftNodes { get; } = new(0);

    internal TreeExpandState RightNodes { get; } = new(0);

    /// <summary>Whether runs of unchanged pairs are left out of the rows.</summary>
    public bool ChangesOnly
    {
        get => changesOnly;
        set => changesOnly = value;
    }

    public event EventHandler? Grew;

    public void NotifyGrew() => Grew?.Invoke(this, EventArgs.Empty);

    public event EventHandler? Closing;

    public void Close() => Closing?.Invoke(this, EventArgs.Empty);

    public JsonDiffCursor NewCursor() => new(this);

    ITreeRowCursor ITreeRowSource.NewCursor() => NewCursor();

    internal JsonDiffDocument Side(bool isLeft) => isLeft ? Left : Right;

    // ── Keys ─────────────────────────────────────────────────────────────────────────────

    public static long RecordKey(int record) => (long)record << RecordShift;

    internal static long RegionKey(int record, int region, long local)
        => ((long)record << RecordShift) | ((long)region << RegionShift) | Math.Min(local, LocalMask);

    internal static int RecordOf(long key) => (int)(key >> RecordShift);

    internal static int RegionOf(long key) => (int)((key >> RegionShift) & 1);

    internal static long LocalOf(long key) => key & LocalMask;

    // ── Records ──────────────────────────────────────────────────────────────────────────

    internal static bool HasOwnRow(in JsonDiffRecord record) => !record.IsRun;

    /// <summary>A descended pair starts expanded, so a change shows down to its leaf; everything
    /// else starts collapsed.</summary>
    internal bool IsExpanded(in JsonDiffRecord record) => record.HasChildRecords ^ recordOverrides.Contains(record.Index);

    internal bool IsVisible(in JsonDiffRecord record) => !(changesOnly && record.IsRun);

    /// <summary>The end of a record's descendants that follow it in the log - all records so far
    /// while its descent is still streaming.</summary>
    internal int SubtreeEndOf(in JsonDiffRecord record) => record.SubtreeEnd < 0 ? Diff.RecordCount : record.SubtreeEnd;

    /// <summary>Where a descended record's children begin in the log: straight after it, or
    /// after the descent for a move paired by similarity.</summary>
    internal static int ChildStart(in JsonDiffRecord record) => record.FirstChild >= 0 ? record.FirstChild : record.Index + 1;

    /// <summary>The exclusive end of a descended record's children in the log.</summary>
    internal int ChildEnd(in JsonDiffRecord record) => record.FirstChild >= 0 ? record.ChildrenEnd : SubtreeEndOf(record);

    /// <summary>
    /// The record whose rows come after all of <paramref name="index"/>'s, in merged order: the
    /// one after its subtree, unless that is past its parent's children - then whatever comes
    /// after the parent. -1 at the end. What makes a block of children emitted after the descent
    /// read as if it sat under its parent.
    /// </summary>
    internal int After(int index)
    {
        while (true)
        {
            var record = Diff.GetRecord(index);
            int next = SubtreeEndOf(record);
            int parent = record.ParentRecord;
            int bound = parent >= 0 ? ChildEnd(Diff.GetRecord(parent)) : Diff.MainRecordCount;
            if (next < bound)
                return next;
            if (parent < 0)
                return -1;
            index = parent;
        }
    }

    /// <summary>Whether <paramref name="record"/> lies beneath <paramref name="ancestor"/>.</summary>
    internal bool IsDescendant(int record, int ancestor)
    {
        for (int at = Diff.GetRecord(record).ParentRecord; at >= 0; at = Diff.GetRecord(at).ParentRecord)
        {
            if (at == ancestor)
                return true;
        }

        return false;
    }

    /// <summary>Document depth of a record's node on one side - the merged depth except beneath
    /// a move across parents.</summary>
    private static int DocDepth(in JsonDiffRecord record, bool isLeft) => isLeft ? record.LeftDepth : record.RightDepth;

    /// <summary>The container kind holding a record's node on one side - the document level at
    /// the top.</summary>
    internal byte ParentKind(in JsonDiffRecord record, bool isLeft)
    {
        int parent = record.ParentRecord;
        if (record.IsCrossParentMove && record.IsMoveSource != isLeft)
            parent = Diff.GetRecord(record.MovePartnerRecord).ParentRecord;
        if (parent < 0)
            return JsonTreeReader.Document;

        var container = Diff.GetRecord(parent);
        var node = isLeft ? container.Left : container.Right;
        return Side(isLeft).Bytes.ByteAt(node.ValueStart) == (byte)'{' ? (byte)JsonTokenKind.StartObject : (byte)JsonTokenKind.StartArray;
    }

    /// <summary>The value start of the container holding a record's nodes on one side, or -1 at
    /// the document level.</summary>
    private long ParentValueStart(in JsonDiffRecord record, bool isLeft)
        => record.ParentRecord < 0 ? -1 : (isLeft ? Diff.GetRecord(record.ParentRecord).Left : Diff.GetRecord(record.ParentRecord).Right).ValueStart;

    /// <summary>
    /// The regions a record shows when it is expanded - see the plan's table: a run's pairs; a
    /// removed or added node's children; both sides' children of a pair changed from one kind of
    /// value to another; the two sides of a range.
    /// </summary>
    internal int Regions(in JsonDiffRecord record, Span<DiffRegion> regions)
    {
        int count = 0;
        if (record.IsRun)
        {
            regions[count++] = SiblingSpan(record, isLeft: true, RegionPanes.Mirror);
        }
        else if (record.IsRange)
        {
            if (record.LeftCount > 0)
                regions[count++] = SiblingSpan(record, isLeft: true, RegionPanes.LeftOnly);
            if (record.RightCount > 0)
                regions[count++] = SiblingSpan(record, isLeft: false, RegionPanes.RightOnly);
        }
        else if (!record.HasChildRecords)
        {
            switch (record.Status)
            {
                case DiffStatus.Removed:
                    AddChildren(record, isLeft: true, ref count, regions);
                    break;
                case DiffStatus.Added:
                    AddChildren(record, isLeft: false, ref count, regions);
                    break;
                case DiffStatus.Moved:
                    // Content lives at one end: the stub keeps its old place on the left, the
                    // destination (and an in-array move) its new one on the right.
                    AddChildren(record, isLeft: record.IsMoveSource, ref count, regions);
                    break;
                case DiffStatus.Modified:
                    AddChildren(record, isLeft: true, ref count, regions);
                    AddChildren(record, isLeft: false, ref count, regions);
                    break;
            }
        }

        return count;
    }

    private DiffRegion SiblingSpan(in JsonDiffRecord record, bool isLeft, RegionPanes panes)
    {
        long parentStart = ParentValueStart(record, isLeft);
        var first = isLeft ? record.Left : record.Right;
        return new DiffRegion(isLeft, DocDepth(record, isLeft) - 1, Math.Max(0, parentStart),
            isLeft ? record.LeftOrdinal : record.RightOrdinal,
            isLeft ? record.LeftCount : record.RightCount,
            first.RowStart,
            (isLeft ? record.LeftEnd : record.RightEnd) - 1,
            record.Depth, panes, record.RightOrdinal);
    }

    private void AddChildren(in JsonDiffRecord record, bool isLeft, ref int count, Span<DiffRegion> regions)
    {
        var at = isLeft ? record.Left : record.Right;
        if (!at.IsPresent)
            return;

        var document = Side(isLeft);
        var node = document.NodeAt(at);
        if (!node.IsContainer)
            return;

        long position = document.Reader.FirstChildPosition(node.ValueStart);
        if (!document.Reader.TryReadChild(node.FormatKind, ref position, out var firstChild, out _))
            return;

        // The record's own end, not the node's read again: a small container's end is a scan.
        long end = isLeft ? record.LeftEnd : record.RightEnd;
        regions[count++] = new DiffRegion(isLeft, DocDepth(record, isLeft), node.ValueStart, 0, long.MaxValue,
            firstChild.RowStart, end - 1, record.Depth + 1,
            isLeft ? RegionPanes.LeftOnly : RegionPanes.RightOnly, -1);
    }

    /// <summary>Whether a record's own row has anything beneath it.</summary>
    internal bool HasChildren(in JsonDiffRecord record)
    {
        if (record.HasChildRecords)
            return true;
        Span<DiffRegion> regions = stackalloc DiffRegion[2];
        return Regions(record, regions) > 0;
    }

    /// <summary>The change a record stands for, if it is one: what next/previous change stops on.
    /// A descended pair is only the path to one, unless its element also moved.</summary>
    internal static bool IsChange(in JsonDiffRecord record) => record.Status switch
    {
        DiffStatus.Added or DiffStatus.Removed or DiffStatus.Moved => true,
        DiffStatus.Modified => !record.HasChildRecords || record.IsMovedWithin,
        _ => false,
    };

    // ── Expansion (ITreeRowSource) ───────────────────────────────────────────────────────

    private static JsonDiffRowDetail DetailOf(in TreeRow row)
        => row.Detail as JsonDiffRowDetail ?? throw new InvalidOperationException("Not a diff row.");

    internal bool IsExpanded(JsonDiffRowDetail detail, in TreeRow row)
        => detail.IsRecordRow ? IsExpanded(Diff.GetRecord(detail.Record)) : row.IsExpanded;

    public void Toggle(in TreeRow row)
    {
        var detail = DetailOf(row);
        if (detail.IsRecordRow)
        {
            if (!recordOverrides.Remove(detail.Record))
                recordOverrides.Add(detail.Record);
            return;
        }

        var pane = detail.RegionIsLeft ? detail.Left : detail.Right;
        if (pane is { } drawn)
            (detail.RegionIsLeft ? LeftNodes : RightNodes).Toggle(drawn.Node.ValueStart);
    }

    public void SetExpanded(in TreeRow row, bool expanded)
    {
        if (row.Shape == TreeRowShape.Open && IsExpanded(DetailOf(row), row) != expanded)
            Toggle(row);
    }

    public bool ExpandDeep(in TreeRow row, int rowBudget)
    {
        SetExpanded(row, true);
        var walker = NewCursor();
        walker.SeekTo(row.Start);

        int budget = rowBudget;
        while (budget-- > 0 && walker.MoveNext() && walker.Current.Depth > row.Depth)
        {
            if (walker.Current is { Shape: TreeRowShape.Open, IsExpanded: false } closed)
            {
                SetExpanded(closed, true);
                walker.SeekTo(closed.Start);
            }
        }

        return budget >= 0;
    }

    public void CollapseDeep(in TreeRow row) => SetExpanded(row, false);

    /// <summary>A collapsed record row hides its descendant records and its regions' rows; a
    /// collapsed region row hides the rows inside its node.</summary>
    public bool Hides(in TreeRow row, long position)
    {
        var detail = DetailOf(row);
        int target = RecordOf(position);
        if (detail.IsRecordRow)
        {
            var record = Diff.GetRecord(detail.Record);
            return (target == detail.Record && LocalOf(position) > 0)
                || (target != detail.Record && target < Diff.RecordCount && IsDescendant(target, detail.Record));
        }

        if (target != detail.Record || RegionOf(position) != detail.Region)
            return false;

        var pane = detail.RegionIsLeft ? detail.Left : detail.Right;
        if (pane is not { } drawn || !drawn.Node.IsContainer)
            return false;

        long offset = detail.RegionBase + LocalOf(position) - 1;
        return offset > drawn.Node.ValueStart && offset < Side(detail.RegionIsLeft).End(drawn.Node) - 1;
    }

    /// <summary>
    /// Makes the row with <paramref name="key"/> visible, opening whatever hides it, and returns it
    /// - what next/previous change and find do before the view scrolls there. Null when there are
    /// no rows.
    /// </summary>
    public TreeRow? Reveal(long key)
    {
        var cursor = NewCursor();
        if (!cursor.SeekTo(key))
            return null;

        while (cursor.Current is { Shape: TreeRowShape.Open, IsExpanded: false } hidden && Hides(hidden, key))
        {
            SetExpanded(hidden, true);
            cursor.SeekTo(key);
        }

        return cursor.Current;
    }

    // ── Scrolling (ITreeRowSource) ───────────────────────────────────────────────────────

    /// <summary>The left document's length: rows are placed on the scrollbar by where they sit in
    /// it.</summary>
    public long ScrollLength => Math.Max(1, Left.Bytes.AvailableLength);

    public long ScrollPosition(in TreeRow row) => DetailOf(row).ScrollPosition;

    public void SeekScrollPosition(ITreeRowCursor cursor, long position)
    {
        if (position <= 0 || Diff.RecordCount == 0)
            cursor.MoveToStart();
        else
            cursor.SeekTo(KeyForLeftOffset(position));
    }

    public bool IsComplete => Diff.AllItemsPublished;

    /// <summary>
    /// The key of the row at <paramref name="offset"/> in the left document: in the last record
    /// anchored at or before it - anchors never decrease along the log, and a record's children
    /// follow it, so that is the deepest record that has started by then - the row of its left
    /// region holding the offset, or else its own row.
    /// </summary>
    internal long KeyForLeftOffset(long offset)
    {
        // The records a move paired by similarity has after the descent are anchored where it
        // is, out of order; they are reached through it.
        int count = Diff.MainRecordCount;
        int low = 0, high = count - 1, found = 0;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (Diff.GetRecord(middle).LeftAnchor <= offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var record = Diff.GetRecord(found);
        Span<DiffRegion> regions = stackalloc DiffRegion[2];
        int regionCount = Regions(record, regions);
        for (int region = 0; region < regionCount; region++)
        {
            var span = regions[region];
            if (span.IsLeft && offset >= span.FirstRowStart && offset <= span.LastByte)
                return RegionKey(found, region, offset - span.Base + 1);
        }

        if (HasOwnRow(record) || regionCount == 0)
            return RecordKey(found);

        // A run the offset lies just past: its last row.
        var last = regions[0];
        return RegionKey(found, 0, Math.Clamp(offset, last.FirstRowStart, last.LastByte) - last.Base + 1);
    }

    // ── Find ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The key of the row that draws the match at <paramref name="offset"/> of one document, for
    /// find to order its stops by and reveal. <c>long.MaxValue</c> when no record covers it yet
    /// (it sorts last rather than blocking the step); null when that side's bytes there are not on
    /// screen at all - inside unchanged content, which is drawn from the left; the far end of a
    /// move; the children of a descended pair that are records rather than nodes.
    /// </summary>
    public long? KeyForMatch(bool leftSide, long offset)
    {
        int owner = FindRecordCovering(leftSide, offset);
        if (owner < 0)
            return long.MaxValue;

        var record = Diff.GetRecord(owner);
        var chain = Side(leftSide).Locate(offset);
        if (chain.Count == 0)
            return null;

        var drawn = leftSide ? record.Left : record.Right;
        var target = chain[^1];
        if (HasOwnRow(record) && !record.IsRange && target.Node.ValueStart == drawn.ValueStart)
            return RecordKey(owner);

        Span<DiffRegion> regions = stackalloc DiffRegion[2];
        int regionCount = Regions(record, regions);
        for (int region = 0; region < regionCount; region++)
        {
            var span = regions[region];
            if (span.IsLeft != leftSide || span.ChildDocDepth >= chain.Count)
                continue;

            // The span's own child holding the match must be one of its siblings.
            var child = chain[span.ChildDocDepth];
            if (child.Node.RowStart < span.FirstRowStart || child.Node.RowStart > span.LastByte || !span.Holds(child.Ordinal))
                continue;

            return RegionKey(owner, region, target.Node.RowStart - span.Base + 1);
        }

        // The record's own node holds the match but no row of it does: its name, for one.
        if (HasOwnRow(record) && !record.IsRange && offset < drawn.ValueStart)
            return RecordKey(owner);

        return null;
    }

    /// <summary>Whether a record draws its node, or a region, on one side - which decides whether
    /// a match there can stop on it.</summary>
    private static bool Draws(in JsonDiffRecord record, bool leftSide)
    {
        var node = leftSide ? record.Left : record.Right;
        if (!node.IsPresent)
            return false;

        return record.Status switch
        {
            DiffStatus.Unchanged => leftSide, // runs draw the left into both panes
            DiffStatus.Moved => record.IsMoveSource == leftSide || (!record.IsCrossParentMove && !leftSide),
            _ => true,
        };
    }

    /// <summary>
    /// The deepest record drawing <paramref name="offset"/> on one side, or -1: a binary search
    /// over the records ordered by where their drawn node starts, then up the parent chain - no
    /// offset-to-record map, which at multi-GB scale would cost as much as the diff itself.
    /// </summary>
    private int FindRecordCovering(bool leftSide, long offset)
    {
        var order = StartOrder(leftSide);
        int low = 0, high = order.Length - 1, found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            var node = leftSide ? Diff.GetRecord(order[middle]).Left : Diff.GetRecord(order[middle]).Right;
            if (node.RowStart <= offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        for (int candidate = found >= 0 ? order[found] : -1; candidate >= 0; candidate = Diff.GetRecord(candidate).ParentRecord)
        {
            var record = Diff.GetRecord(candidate);
            var node = leftSide ? record.Left : record.Right;
            long end = leftSide ? record.LeftEnd : record.RightEnd;
            if (Draws(record, leftSide) && offset >= node.RowStart && offset < end)
                return candidate;
        }

        return -1;
    }

    /// <summary>
    /// Record indices that draw this side, ordered by where their node starts. Built lazily, only
    /// if find is actually used, and again if the log has changed since - it grows while the diff
    /// runs, and the move pass rewrites records when it ends. One int per record per side.
    /// </summary>
    private int[] StartOrder(bool leftSide)
    {
        var version = (Diff.RecordCount, Diff.AllItemsPublished);
        ref int[]? cached = ref leftSide ? ref leftStartOrder : ref rightStartOrder;
        ref (int, bool) cachedVersion = ref leftSide ? ref leftStartOrderVersion : ref rightStartOrderVersion;
        if (cached is not null && cachedVersion == version)
            return cached;

        var order = new List<int>(version.RecordCount);
        for (int i = 0; i < version.RecordCount; i++)
        {
            if (Draws(Diff.GetRecord(i), leftSide))
                order.Add(i);
        }

        var array = order.ToArray();
        Array.Sort(array, (a, b) => (leftSide ? Diff.GetRecord(a).Left : Diff.GetRecord(a).Right).RowStart
            .CompareTo((leftSide ? Diff.GetRecord(b).Left : Diff.GetRecord(b).Right).RowStart));
        cached = array;
        cachedVersion = version;
        return array;
    }

    // ── Next / previous change ───────────────────────────────────────────────────────────

    /// <summary>The key of the next (direction +1) or previous (-1) change after the record of
    /// <paramref name="fromKey"/> (null = before the start), in merged order, wrapping around; null
    /// when there are none. Walks the records rather than the rows, so a change under a collapsed
    /// row is found.</summary>
    public long? NextChange(long? fromKey, int direction)
    {
        var order = MergedOrder();
        if (order.Length == 0)
            return null;

        int from = direction > 0 ? -1 : order.Length;
        if (fromKey is { } key)
        {
            int record = RecordOf(key);
            int at = System.Array.IndexOf(order, record);
            if (at >= 0)
                from = at;
        }

        for (int step = 1; step <= order.Length; step++)
        {
            int position = (((from + step * direction) % order.Length) + order.Length) % order.Length;
            if (IsChange(Diff.GetRecord(order[position])))
                return RecordKey(order[position]);
        }

        return null;
    }

    private int[]? mergedOrder;
    private (int, bool) mergedOrderVersion;

    /// <summary>Every record in merged order - the log's order, with each block of children
    /// emitted after the descent read in under its parent. Rebuilt when the log has changed.</summary>
    private int[] MergedOrder()
    {
        var version = (Diff.RecordCount, Diff.AllItemsPublished);
        if (mergedOrder is not null && mergedOrderVersion == version)
            return mergedOrder;

        var order = new List<int>(version.RecordCount);
        int index = version.RecordCount > 0 ? 0 : -1;
        while (index >= 0)
        {
            order.Add(index);
            var record = Diff.GetRecord(index);
            index = record.HasChildRecords && ChildStart(record) < ChildEnd(record) ? ChildStart(record) : After(index);
        }

        mergedOrder = order.ToArray();
        mergedOrderVersion = version;
        return mergedOrder;
    }
}
