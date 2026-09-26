using System.Collections.Generic;

namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// Stands on one display row of a tree and steps to its neighbours, reading the document on
/// demand through a format's <see cref="ITreeFormatReader"/> and jumping through a
/// <see cref="SparseContainerIndex"/>. Nothing outside the rows it is asked for is materialised,
/// so a view over it costs memory for its viewport, not for the document or the expanded tree.
///
/// Rows follow the expanded tree in document order: a container's open row, then - if expanded -
/// its children's rows and its close row. A collapsed container is one row; stepping past it
/// jumps its bytes by the index's recorded end or the reader's skip.
///
/// <list type="bullet">
/// <item><b>Forward</b> is reading on from the current row.</item>
/// <item><b>Backward</b> finds the previous sibling's start from the nearest resume point in the
/// parent - a checkpoint, or the parent's opening - and takes that sibling's last row: its close
/// row when expanded. It never walks the rows between, only the bytes, skipping each sibling
/// whole.</item>
/// <item><b>Seek</b> resolves an offset to its innermost recorded container, rebuilds the
/// ancestor chain from the index, and reads forward from the nearest resume point, descending
/// only into expanded containers that hold the target.</item>
/// </list>
///
/// Single-threaded, like the view that drives it; the index it reads may still be growing.
/// </summary>
public sealed class TreeCursor
{
    private struct Frame
    {
        public TreeNode Node;
        public int Depth;
        public long Ordinal;
        public byte ParentKind;

        /// <summary>The container's record in the index, -1 when not recorded, or -2 until
        /// looked up.</summary>
        public int Record;
    }

    private const int NotLookedUp = -2;

    /// <summary>Sibling nodes kept from the last walk through a container - enough that paging
    /// back through a flat array of tiny values reads each run of siblings once, not once per
    /// row.</summary>
    private const int SiblingCacheSize = 1024;

    private readonly SparseContainerIndex index;
    private readonly ITreeFormatReader reader;
    private readonly TreeExpandState expandState;

    // The containers enclosing the current row, outermost first, starting with the document
    // level. A close row's container is not among them: the close row sits beside its open row.
    private readonly List<Frame> frames = new();

    // The last siblings a walk passed, for one container: ordinals siblingsFirstOrdinal onward.
    // Nodes never change - the bytes do not - so nothing invalidates them.
    // A ring: the sibling with ordinal o sits at (o % SiblingCacheSize), for o in
    // [siblingsFirstOrdinal, siblingsFirstOrdinal + siblingsCount).
    private readonly TreeNode[] siblings = new TreeNode[SiblingCacheSize];
    private int siblingsCount;
    private long siblingsContainer = long.MinValue;
    private long siblingsFirstOrdinal;

    public TreeCursor(SparseContainerIndex index, ITreeFormatReader reader, TreeExpandState expandState)
    {
        this.index = index;
        this.reader = reader;
        this.expandState = expandState;
    }

    /// <summary>The row the cursor stands on. Meaningful once a move or seek has returned
    /// true.</summary>
    public TreeRow Current { get; private set; }

    /// <summary>The nodes of the containers enclosing <see cref="Current"/>, outermost first.
    /// Not including the document level.</summary>
    public IEnumerable<TreeNode> Ancestors
    {
        get
        {
            for (int i = 1; i < frames.Count; i++)
                yield return frames[i].Node;
        }
    }

    /// <summary>Stands on the document's first row; false for an empty document.</summary>
    public bool MoveToStart()
    {
        ResetToDocument();
        long position = 0;
        if (!reader.TryReadChild(reader.DocumentKind, ref position, out var first, out _))
            return false;

        Current = RowFor(first, 0);
        return true;
    }

    /// <summary>Stands on the document's last row; false for an empty document.</summary>
    public bool MoveToEnd() => SeekTo(long.MaxValue);

    public bool MoveNext()
    {
        var row = Current;
        long position;
        long ordinal;

        switch (row.Shape)
        {
            case TreeRowShape.Open when row.IsExpanded:
                Push(row.Node, row.Depth, row.Ordinal, row.ParentKind, NotLookedUp);
                position = reader.FirstChildPosition(row.Node.ValueStart);
                ordinal = 0;
                break;
            case TreeRowShape.Close:
                position = ContainerEnd(row.Node.ValueStart);
                ordinal = row.Ordinal + 1;
                break;
            default:
                position = NodeEnd(row.Node);
                ordinal = row.Ordinal + 1;
                break;
        }

        return ReadNextIn(position, ordinal);
    }

    public bool MovePrevious()
    {
        var row = Current;

        if (row.Shape == TreeRowShape.Close)
        {
            // Into the container: its last child's last row, or its open row when it is empty.
            Push(row.Node, row.Depth, row.Ordinal, row.ParentKind, NotLookedUp);
            if (FindLastChild(out var last, out long lastOrdinal))
            {
                Current = LastRowOf(last, lastOrdinal);
                return true;
            }

            PopToOpenRow();
            return true;
        }

        if (row.Ordinal == 0)
        {
            if (frames.Count == 1)
                return false;

            PopToOpenRow();
            return true;
        }

        var previous = FindChild(frames.Count - 1, row.Ordinal - 1);
        Current = LastRowOf(previous, row.Ordinal - 1);
        return true;
    }

    /// <summary>
    /// Stands on the row showing <paramref name="offset"/>: the deepest visible row whose bytes
    /// hold it - a collapsed ancestor's open row if it is hidden - or, when it falls between rows,
    /// the next one. Past the end, the last row. False only for an empty document.
    /// </summary>
    public bool SeekTo(long offset)
    {
        ResetToDocument();
        long position = 0;
        long ordinal = 0;

        var resume = index.FindResumePoint(offset);
        if (resume.Container >= 0)
        {
            // Rebuild the recorded ancestor chain from the index. Each one's row (its name, for a
            // member) is read from its parent, and a collapsed one ends the seek on its row.
            var chain = new List<int>();
            for (int record = resume.Container; record >= 0; record = index.GetContainer(record).Parent)
                chain.Add(record);

            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var container = index.GetContainer(chain[i]);
                var node = FindChild(frames.Count - 1, container.OrdinalInParent);
                var row = RowFor(node, container.OrdinalInParent);
                if (!row.IsExpanded)
                {
                    Current = row;
                    return true;
                }

                Push(node, row.Depth, row.Ordinal, row.ParentKind, chain[i]);
            }

            // On the innermost container's own opening: its open row, not its first child.
            if (offset < reader.FirstChildPosition(frames[^1].Node.ValueStart))
            {
                PopToOpenRow();
                return true;
            }

            position = resume.AtOpen ? reader.FirstChildPosition(resume.Offset) : resume.Offset;
            ordinal = resume.AtOpen ? 0 : resume.Ordinal;
        }

        TreeNode lastTopLevel = default;
        long lastTopLevelOrdinal = -1;

        while (true)
        {
            var parent = frames[^1];
            if (!reader.TryReadChild(parent.Node.FormatKind, ref position, out var child, out _))
            {
                if (frames.Count > 1)
                {
                    PopToCloseRow();
                    return true;
                }

                // Past the last top-level value: stand on the document's last row.
                if (lastTopLevelOrdinal < 0)
                    return false;

                Current = LastRowOf(lastTopLevel, lastTopLevelOrdinal);
                return true;
            }

            long end = NodeEnd(child);
            if (offset < end)
            {
                var row = RowFor(child, ordinal);
                if (row.Shape == TreeRowShape.Open && row.IsExpanded && offset >= reader.FirstChildPosition(child.ValueStart))
                {
                    Push(child, row.Depth, row.Ordinal, row.ParentKind, NotLookedUp);
                    position = reader.FirstChildPosition(child.ValueStart);
                    ordinal = 0;
                    continue;
                }

                Current = row;
                return true;
            }

            if (frames.Count == 1)
            {
                lastTopLevel = child;
                lastTopLevelOrdinal = ordinal;
            }

            position = end;
            ordinal++;
        }
    }

    private bool ReadNextIn(long position, long ordinal)
    {
        var parent = frames[^1];
        if (reader.TryReadChild(parent.Node.FormatKind, ref position, out var child, out _))
        {
            Current = RowFor(child, ordinal);
            return true;
        }

        if (frames.Count == 1)
            return false;

        PopToCloseRow();
        return true;
    }

    /// <summary>The row a node shows as a child of the innermost frame.</summary>
    private TreeRow RowFor(TreeNode node, long ordinal)
    {
        var parent = frames[^1];
        int depth = frames.Count - 1;
        bool expanded = node.IsContainer && expandState.IsExpanded(node.ValueStart, depth);
        return new TreeRow(node.IsContainer ? TreeRowShape.Open : TreeRowShape.Leaf, node, node.RowStart, depth, ordinal,
            parent.Node.FormatKind, expanded);
    }

    /// <summary>The last row a child of the innermost frame shows: its close row when it is an
    /// expanded container, else its own row.</summary>
    private TreeRow LastRowOf(TreeNode node, long ordinal)
    {
        var row = RowFor(node, ordinal);
        if (row.Shape != TreeRowShape.Open || !row.IsExpanded)
            return row;

        long end = ContainerEnd(node.ValueStart);
        return row with { Shape = TreeRowShape.Close, Start = reader.CloseStart(node.ValueStart, end) };
    }

    /// <summary>Leaves the innermost container for its open row.</summary>
    private void PopToOpenRow()
    {
        var container = frames[^1];
        frames.RemoveAt(frames.Count - 1);
        Current = new TreeRow(TreeRowShape.Open, container.Node, container.Node.RowStart, container.Depth, container.Ordinal,
            container.ParentKind, IsExpanded: true);
    }

    /// <summary>Leaves the innermost container for its close row.</summary>
    private void PopToCloseRow()
    {
        var container = frames[^1];
        frames.RemoveAt(frames.Count - 1);
        long end = ContainerEnd(container.Node.ValueStart);
        Current = new TreeRow(TreeRowShape.Close, container.Node, reader.CloseStart(container.Node.ValueStart, end),
            container.Depth, container.Ordinal, container.ParentKind, IsExpanded: true);
    }

    /// <summary>Child <paramref name="ordinal"/> of the frame at <paramref name="frameIndex"/>,
    /// read from the nearest known point before it, skipping the children between whole. The
    /// siblings passed on the way are kept, so the next few earlier ordinals cost nothing.</summary>
    private TreeNode FindChild(int frameIndex, long ordinal)
    {
        long container = frames[frameIndex].Node.ValueStart;
        if (container == siblingsContainer && ordinal >= siblingsFirstOrdinal && ordinal < siblingsFirstOrdinal + siblingsCount)
            return siblings[ordinal % SiblingCacheSize];

        var (position, at) = ResumeIn(frameIndex, ordinal);
        byte kind = frames[frameIndex].Node.FormatKind;
        siblingsCount = 0;
        siblingsContainer = container;
        siblingsFirstOrdinal = at;

        while (true)
        {
            if (!reader.TryReadChild(kind, ref position, out var child, out _))
            {
                siblingsContainer = long.MinValue;
                throw new System.InvalidOperationException($"Child {ordinal} not found; the container ended after {at}.");
            }

            siblings[at % SiblingCacheSize] = child;
            if (siblingsCount == SiblingCacheSize)
                siblingsFirstOrdinal++;
            else
                siblingsCount++;

            if (at == ordinal)
                return child;

            position = NodeEnd(child);
            at++;
        }
    }

    /// <summary>The innermost frame's last child, or false when it has none.</summary>
    private bool FindLastChild(out TreeNode last, out long lastOrdinal)
    {
        int frameIndex = frames.Count - 1;
        int record = RecordOf(frameIndex);
        if (record >= 0 && index.GetContainer(record) is { IsOpen: false } recorded)
        {
            lastOrdinal = recorded.ChildCount - 1;
            last = lastOrdinal >= 0 ? FindChild(frameIndex, lastOrdinal) : default;
            return lastOrdinal >= 0;
        }

        // A small container: read it through.
        var (position, at) = ResumeIn(frameIndex, 0);
        byte kind = frames[frameIndex].Node.FormatKind;
        last = default;
        lastOrdinal = -1;
        while (reader.TryReadChild(kind, ref position, out var child, out _))
        {
            last = child;
            lastOrdinal = at++;
            position = NodeEnd(child);
        }

        return lastOrdinal >= 0;
    }

    /// <summary>Where to start reading the frame's children to reach child
    /// <paramref name="ordinal"/>, and the ordinal found there.</summary>
    private (long Position, long Ordinal) ResumeIn(int frameIndex, long ordinal)
    {
        if (frameIndex == 0)
            return (0, 0);

        var frame = frames[frameIndex];
        int record = RecordOf(frameIndex);
        if (record < 0)
            return (reader.FirstChildPosition(frame.Node.ValueStart), 0);

        var resume = index.FindResumePoint(record, ordinal);
        return resume.AtOpen
            ? (reader.FirstChildPosition(frame.Node.ValueStart), 0)
            : (resume.Offset, resume.Ordinal);
    }

    private int RecordOf(int frameIndex)
    {
        var frame = frames[frameIndex];
        if (frame.Record == NotLookedUp)
        {
            frame.Record = index.FindContainerStartingAt(frame.Node.ValueStart);
            frames[frameIndex] = frame;
        }

        return frame.Record;
    }

    private long NodeEnd(TreeNode node) => node.IsContainer ? ContainerEnd(node.ValueStart) : node.ValueEnd;

    private long ContainerEnd(long containerStart)
    {
        int record = index.FindContainerStartingAt(containerStart);
        if (record >= 0 && index.GetContainer(record).End is var end and >= 0)
            return end;

        return reader.SkipValue(containerStart);
    }

    private void Push(TreeNode node, int depth, long ordinal, byte parentKind, int record) =>
        frames.Add(new Frame { Node = node, Depth = depth, Ordinal = ordinal, ParentKind = parentKind, Record = record });

    private void ResetToDocument()
    {
        frames.Clear();
        var document = new TreeNode(0, -1, -1, IsContainer: true, reader.DocumentKind);
        frames.Add(new Frame { Node = document, Depth = -1, Ordinal = 0, ParentKind = reader.DocumentKind, Record = -1 });
    }
}
