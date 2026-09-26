using System;
using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Stands on one row of the merged diff tree and steps to its neighbours. The rows are the diff's
/// records in log order - which is merged depth-first order - and, inside an expanded record,
/// the rows of its regions (see <see cref="JsonDiffTree.Regions"/>), which it walks by handing
/// over to a <see cref="TreeCursor"/> on the region's side and back. Nothing beyond the current
/// row is held: a record is read from the log when it is reached, a region's rows from the bytes.
///
/// The merged tree has no closing rows: a region's walker skips its own.
///
/// Stepping over records uses the log's shape alone: a record's children follow it up to its
/// <c>SubtreeEnd</c>, so the next row after a record's subtree is the record there, and the row
/// before a record is its parent's or its previous sibling's last. Neither walks the rows in
/// between.
/// </summary>
public sealed class JsonDiffCursor : ITreeRowCursor
{
    private readonly JsonDiffTree tree;

    // Where the cursor stands: a record's own row (region -1), or a row of one of its regions,
    // read by the walker.
    private int record = -1;
    private int region = -1;
    private DiffRegion regionSpan;
    private TreeCursor? walker;

    internal JsonDiffCursor(JsonDiffTree tree) => this.tree = tree;

    public TreeRow Current { get; private set; }

    private JsonDiffIndex Diff => tree.Diff;

    public JsonDiffCursor Clone()
    {
        var clone = new JsonDiffCursor(tree)
        {
            record = record,
            region = region,
            regionSpan = regionSpan,
            walker = walker?.Clone(),
            Current = Current,
        };
        return clone;
    }

    ITreeRowCursor ITreeRowCursor.Clone() => Clone();

    public IEnumerable<TreeRow> Ancestors
    {
        get
        {
            if (record < 0)
                yield break;

            var chain = new List<int>();
            for (int ancestor = Diff.GetRecord(record).ParentRecord; ancestor >= 0; ancestor = Diff.GetRecord(ancestor).ParentRecord)
                chain.Add(ancestor);

            for (int i = chain.Count - 1; i >= 0; i--)
                yield return RecordRow(chain[i]);

            if (region < 0 || walker is null)
                yield break;

            if (JsonDiffTree.HasOwnRow(Diff.GetRecord(record)))
                yield return RecordRow(record);

            foreach (var ancestor in walker.Ancestors)
            {
                if (ancestor.Depth >= regionSpan.ChildDocDepth)
                    yield return RegionRow(ancestor);
            }
        }
    }

    // ── Moves ────────────────────────────────────────────────────────────────────────────

    public bool MoveToStart() => EnterForward(0);

    public bool MoveToEnd()
    {
        int count = Diff.RecordCount;
        if (count == 0)
            return false;

        // The last top-level record: follow the top level's siblings along.
        int last = 0;
        int main = Diff.MainRecordCount;
        for (int next = tree.SubtreeEndOf(Diff.GetRecord(0)); next < main; next = tree.SubtreeEndOf(Diff.GetRecord(next)))
            last = next;

        return EnterLast(last) || BeforeRecord(last);
    }

    public bool MoveNext()
    {
        if (record < 0)
            return false;

        long from = Current.Start;
        if (StepForward())
            return true;

        // Rows ran out: the walk above may have moved the walker; put it back.
        SeekTo(from);
        return false;
    }

    public bool MovePrevious()
    {
        if (record < 0)
            return false;

        long from = Current.Start;
        if (StepBackward())
            return true;

        SeekTo(from);
        return false;
    }

    private bool StepForward()
    {
        if (record < 0)
            return false;

        var current = Diff.GetRecord(record);
        if (region < 0)
        {
            if (tree.IsExpanded(current))
            {
                if (current.HasChildRecords)
                    return EnterForward(JsonDiffTree.ChildStart(current));

                if (EnterRegionAfter(current, -1))
                    return true;
            }

            return EnterForward(tree.After(record));
        }

        if (WalkerNext())
            return true;

        return EnterRegionAfter(current, region) || EnterForward(tree.After(record));
    }

    private bool StepBackward()
    {
        if (record < 0)
            return false;

        var current = Diff.GetRecord(record);
        if (region < 0)
            return BeforeRecord(record);

        if (WalkerPrevious())
            return true;

        for (int earlier = region - 1; earlier >= 0; earlier--)
        {
            if (EnterRegionLast(current, earlier))
                return true;
        }

        if (JsonDiffTree.HasOwnRow(current))
        {
            StandOnRecord(record);
            return true;
        }

        return BeforeRecord(record);
    }

    /// <summary>Stands on the first row of record <paramref name="index"/>, or of the first
    /// record after it in merged order that shows one; -1 is the end.</summary>
    private bool EnterForward(int index)
    {
        int count = Diff.RecordCount;
        while (index >= 0 && index < count)
        {
            var candidate = Diff.GetRecord(index);
            if (!tree.IsVisible(candidate))
            {
                index = tree.After(index);
                continue;
            }

            if (JsonDiffTree.HasOwnRow(candidate))
            {
                StandOnRecord(index);
                return true;
            }

            if (EnterRegionAfter(candidate, -1))
                return true;

            index = tree.After(index);
        }

        return false;
    }

    /// <summary>The row before record <paramref name="index"/>'s first: its parent's own row
    /// when it is the first child, else the last row of the nearest earlier sibling showing
    /// one.</summary>
    private bool BeforeRecord(int index)
    {
        int parent = Diff.GetRecord(index).ParentRecord;
        int first = parent >= 0 ? JsonDiffTree.ChildStart(Diff.GetRecord(parent)) : 0;
        int current = index;
        while (current > first)
        {
            // The record before this one belongs to the previous sibling's subtree: climb to it.
            int sibling = current - 1;
            while (Diff.GetRecord(sibling).ParentRecord != parent)
                sibling = Diff.GetRecord(sibling).ParentRecord;

            if (EnterLast(sibling))
                return true;

            current = sibling;
        }

        if (parent >= 0)
        {
            StandOnRecord(parent);
            return true;
        }

        return false;
    }

    /// <summary>Stands on the last row of record <paramref name="index"/>'s subtree as shown;
    /// false when it shows none.</summary>
    private bool EnterLast(int index)
    {
        var candidate = Diff.GetRecord(index);
        if (!tree.IsVisible(candidate))
            return false;

        if (!JsonDiffTree.HasOwnRow(candidate))
            return EnterRegionBefore(candidate, int.MaxValue);

        if (tree.IsExpanded(candidate))
        {
            if (candidate.HasChildRecords)
            {
                int first = JsonDiffTree.ChildStart(candidate);
                int end = tree.ChildEnd(candidate);
                if (end > first)
                {
                    // Its last child is the ancestor of the children's last record just below it.
                    int child = end - 1;
                    while (Diff.GetRecord(child).ParentRecord != index)
                        child = Diff.GetRecord(child).ParentRecord;

                    while (true)
                    {
                        if (EnterLast(child))
                            return true;

                        int earlier = child - 1;
                        if (earlier < first)
                            break;
                        while (Diff.GetRecord(earlier).ParentRecord != index)
                            earlier = Diff.GetRecord(earlier).ParentRecord;
                        child = earlier;
                    }
                }
            }
            else if (EnterRegionBefore(candidate, int.MaxValue))
            {
                return true;
            }
        }

        StandOnRecord(index);
        return true;
    }

    // ── Regions ──────────────────────────────────────────────────────────────────────────

    /// <summary>Enters the first region after <paramref name="after"/> that has rows, at its
    /// first row.</summary>
    private bool EnterRegionAfter(in JsonDiffRecord owner, int after)
    {
        Span<DiffRegion> regions = stackalloc DiffRegion[2];
        int count = tree.Regions(owner, regions);
        for (int next = after + 1; next < count; next++)
        {
            var span = regions[next];
            var cursor = Walker(span);
            if (!cursor.SeekTo(span.FirstRowStart) || !InSpan(cursor.Current, span))
                continue;

            Enter(owner.Index, next, span, cursor);
            return true;
        }

        return false;
    }

    /// <summary>Enters the last region before <paramref name="before"/> that has rows, at its
    /// last row.</summary>
    private bool EnterRegionBefore(in JsonDiffRecord owner, int before)
    {
        Span<DiffRegion> regions = stackalloc DiffRegion[2];
        int count = tree.Regions(owner, regions);
        for (int earlier = Math.Min(before, count) - 1; earlier >= 0; earlier--)
        {
            if (EnterRegionLast(owner, earlier))
                return true;
        }

        return false;
    }

    private bool EnterRegionLast(in JsonDiffRecord owner, int index)
    {
        Span<DiffRegion> regions = stackalloc DiffRegion[2];
        if (index >= tree.Regions(owner, regions))
            return false;

        var span = regions[index];
        var cursor = Walker(span);
        if (!cursor.SeekTo(span.LastByte))
            return false;

        // The last byte is a closing bracket when the last child is an open container, or when
        // the span is a whole container's children: step back to the last row drawn.
        while (cursor.Current.Shape == TreeRowShape.Close)
        {
            if (!cursor.MovePrevious())
                return false;
        }

        if (!InSpan(cursor.Current, span))
            return false;

        Enter(owner.Index, index, span, cursor);
        return true;
    }

    /// <summary>A walker over one side for a region: everything above the span open, the user's
    /// own choices beneath it.</summary>
    private TreeCursor Walker(in DiffRegion span)
    {
        var document = tree.Side(span.IsLeft);
        var nodes = span.IsLeft ? tree.LeftNodes : tree.RightNodes;
        return new TreeCursor(document.Index.Structure, document.Reader, nodes.WithDefaultDepth(span.ChildDocDepth));
    }

    private static bool InSpan(in TreeRow row, in DiffRegion span)
        => row.Shape != TreeRowShape.Close && row.Depth >= span.ChildDocDepth
           && (row.Depth > span.ChildDocDepth || span.Holds(row.Ordinal))
           && row.Start >= span.FirstRowStart && row.Start <= span.LastByte;

    private void Enter(int owner, int index, in DiffRegion span, TreeCursor cursor)
    {
        record = owner;
        region = index;
        regionSpan = span;
        walker = cursor;
        Current = RegionRow(cursor.Current);
    }

    private bool WalkerNext()
    {
        var span = regionSpan;
        while (walker!.MoveNext())
        {
            var row = walker.Current;
            if (row.Depth < span.ChildDocDepth)
                return false;
            if (row.Shape == TreeRowShape.Close)
                continue;
            if (!InSpan(row, span))
                return false;

            Current = RegionRow(row);
            return true;
        }

        return false;
    }

    private bool WalkerPrevious()
    {
        var span = regionSpan;
        var at = walker!.Current;

        // At the span's first child: whatever comes before it is outside the region.
        if (at.Depth == span.ChildDocDepth && at.Ordinal == span.FirstOrdinal)
            return false;

        while (walker.MovePrevious())
        {
            var row = walker.Current;
            if (row.Depth < span.ChildDocDepth)
                return false;
            if (row.Shape == TreeRowShape.Close)
                continue;

            Current = RegionRow(row);
            return true;
        }

        return false;
    }

    // ── Seeking ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stands on the row with key <paramref name="position"/>, or the one showing it: a collapsed
    /// ancestor's row when it is hidden, the nearest row of its region when the key falls between
    /// rows, the next row shown when its record is not (unchanged rows filtered out).
    /// </summary>
    public bool SeekTo(long position)
    {
        int count = Diff.RecordCount;
        if (count == 0)
            return false;

        int target = JsonDiffTree.RecordOf(position);
        if (target >= count || target < 0)
            return MoveToEnd();

        // A collapsed ancestor hides the target: stand on it.
        var chain = new List<int>();
        for (int ancestor = Diff.GetRecord(target).ParentRecord; ancestor >= 0; ancestor = Diff.GetRecord(ancestor).ParentRecord)
            chain.Add(ancestor);

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (!tree.IsExpanded(Diff.GetRecord(chain[i])))
            {
                StandOnRecord(chain[i]);
                return true;
            }
        }

        var owner = Diff.GetRecord(target);
        if (!tree.IsVisible(owner))
            return EnterForward(target) || MoveToEnd();

        long local = JsonDiffTree.LocalOf(position);
        bool ownRow = JsonDiffTree.HasOwnRow(owner);
        if (ownRow && (local == 0 || !tree.IsExpanded(owner)))
        {
            StandOnRecord(target);
            return true;
        }

        Span<DiffRegion> regions = stackalloc DiffRegion[2];
        int regionCount = tree.Regions(owner, regions);
        int index = Math.Min(JsonDiffTree.RegionOf(position), regionCount - 1);
        if (index < 0)
        {
            if (ownRow)
            {
                StandOnRecord(target);
                return true;
            }

            return EnterForward(target) || MoveToEnd();
        }

        var span = regions[index];
        long offset = Math.Clamp(span.Base + local - 1, span.FirstRowStart, span.LastByte);
        var cursor = Walker(span);
        if (!cursor.SeekTo(offset))
            return EnterForward(target);

        // Between two rows, or on a closing bracket: the row it belongs with.
        var landed = cursor.Current;
        if (landed.Shape == TreeRowShape.Close)
        {
            cursor.SeekTo(landed.Node.RowStart);
            landed = cursor.Current;
        }

        if (!InSpan(landed, span))
        {
            if (landed.Start < span.FirstRowStart || landed.Depth < span.ChildDocDepth)
                cursor.SeekTo(span.FirstRowStart);
            else
                return EnterRegionLast(owner, index) || EnterForward(target);
        }

        Enter(target, index, span, cursor);
        return true;
    }

    // ── Rows ─────────────────────────────────────────────────────────────────────────────

    private void StandOnRecord(int index)
    {
        record = index;
        region = -1;
        walker = null;
        Current = RecordRow(index);
    }

    /// <summary>A record's own row, as the painter and the context bar need it.</summary>
    private TreeRow RecordRow(int index)
    {
        var owner = Diff.GetRecord(index);
        bool hasChildren = tree.HasChildren(owner);
        bool expanded = hasChildren && tree.IsExpanded(owner);

        DiffPane? left = null, right = null;
        var leftTint = TreeRowTint.None;
        var rightTint = TreeRowTint.None;
        byte kind = 0;
        bool isContainer = false;

        if (!owner.IsRange)
        {
            bool showsLeft = owner.Left.IsPresent && (owner.Status != DiffStatus.Moved || owner.IsMoveSource);
            bool showsRight = owner.Right.IsPresent && (owner.Status != DiffStatus.Moved || !owner.IsMoveSource);
            if (showsLeft)
            {
                var node = tree.Left.NodeAt(owner.Left);
                left = new DiffPane(node, owner.LeftOrdinal, tree.ParentKind(owner, isLeft: true));
                (kind, isContainer) = (node.FormatKind, node.IsContainer);
            }

            if (showsRight)
            {
                var node = tree.Right.NodeAt(owner.Right);
                right = new DiffPane(node, owner.RightOrdinal, tree.ParentKind(owner, isLeft: false));
                if (!showsLeft)
                    (kind, isContainer) = (node.FormatKind, node.IsContainer);
            }

            (leftTint, rightTint) = owner.Status switch
            {
                DiffStatus.Added => (TreeRowTint.None, TreeRowTint.Added),
                DiffStatus.Removed => (TreeRowTint.Removed, TreeRowTint.None),
                DiffStatus.Moved => owner.IsMoveSource ? (TreeRowTint.Moved, TreeRowTint.None) : (TreeRowTint.None, TreeRowTint.Moved),
                DiffStatus.Modified when !owner.HasChildRecords => (TreeRowTint.Changed, TreeRowTint.Changed),
                _ => (TreeRowTint.None, TreeRowTint.None),
            };
        }

        long key = JsonDiffTree.RecordKey(index);
        var detail = new JsonDiffRowDetail
        {
            Record = index,
            Region = -1,
            Left = left,
            Right = right,
            LeftTint = leftTint,
            RightTint = rightTint,
            IsChangedPath = owner.HasChildRecords,
            IsValueChanged = owner.Status == DiffStatus.Modified && !owner.HasChildRecords,
            ScrollPosition = ScrollPositionOf(owner, owner.Left.IsPresent && owner.Status != DiffStatus.Moved ? owner.Left.RowStart : -1),
        };

        return new TreeRow(hasChildren ? TreeRowShape.Open : TreeRowShape.Leaf,
            new TreeNode(key, key, -1, isContainer, kind), key, owner.Depth, 0, 0, -1, expanded, detail);
    }

    /// <summary>A row of the current region, from the walker's row on the region's side.</summary>
    private TreeRow RegionRow(in TreeRow sideRow)
    {
        var span = regionSpan;
        var owner = Diff.GetRecord(record);
        long key = JsonDiffTree.RegionKey(record, region, sideRow.Start - span.Base + 1);
        var pane = new DiffPane(sideRow.Node, sideRow.Ordinal, sideRow.ParentKind);

        DiffPane? left = null, right = null;
        long mirrorElement = -1, mirrorRightOrdinal = -1;
        switch (span.Panes)
        {
            case RegionPanes.LeftOnly:
                left = pane;
                break;
            case RegionPanes.RightOnly:
                right = pane;
                break;
            default:
                left = pane;
                bool isElement = sideRow.Depth == span.ChildDocDepth;
                long rightOrdinal = isElement ? span.RightFirstOrdinal + (sideRow.Ordinal - span.FirstOrdinal) : sideRow.Ordinal;
                right = pane with { Ordinal = rightOrdinal };
                foreach (var ancestor in walker!.Ancestors)
                {
                    if (ancestor.Depth == span.ChildDocDepth)
                    {
                        mirrorElement = ancestor.Node.ValueStart;
                        mirrorRightOrdinal = span.RightFirstOrdinal + (ancestor.Ordinal - span.FirstOrdinal);
                        break;
                    }
                }

                if (isElement)
                {
                    mirrorElement = sideRow.Node.ValueStart;
                    mirrorRightOrdinal = rightOrdinal;
                }

                break;
        }

        var tint = owner.Status switch
        {
            DiffStatus.Added => TreeRowTint.Added,
            DiffStatus.Removed => TreeRowTint.Removed,
            DiffStatus.Moved => TreeRowTint.Moved,
            _ => TreeRowTint.None,
        };

        var detail = new JsonDiffRowDetail
        {
            Record = record,
            Region = region,
            Left = left,
            Right = right,
            RightMirrorsLeft = span.Panes == RegionPanes.Mirror,
            LeftTint = left is null ? TreeRowTint.None : tint,
            RightTint = right is null ? TreeRowTint.None : tint,
            RegionBase = span.Base,
            RegionIsLeft = span.IsLeft,
            ScrollPosition = ScrollPositionOf(owner, span.IsLeft ? sideRow.Start : -1),
            MirrorLeftElement = mirrorElement,
            MirrorRightOrdinal = mirrorRightOrdinal,
        };

        return new TreeRow(sideRow.Shape, new TreeNode(key, key, -1, sideRow.Node.IsContainer, sideRow.Node.FormatKind),
            key, span.MergedDepth + (sideRow.Depth - span.ChildDocDepth), sideRow.Ordinal, sideRow.ParentKind, -1,
            sideRow.IsExpanded, detail);
    }

    /// <summary>Where a row sits on the scrollbar: its own left offset when its record stands
    /// where its left node is, otherwise where the record is anchored.</summary>
    private static long ScrollPositionOf(in JsonDiffRecord owner, long leftStart)
    {
        bool inPlace = owner.Left.IsPresent && owner.LeftAnchor == owner.Left.RowStart;
        return inPlace && leftStart >= 0 ? leftStart : owner.LeftAnchor;
    }
}
