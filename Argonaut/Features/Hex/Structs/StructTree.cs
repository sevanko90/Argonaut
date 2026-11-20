using System;
using System.Collections.Generic;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// A mapped struct as <see cref="TreeSurface"/> draws it: the rows of <see cref="StructNodes"/>,
/// opened and closed here, their values read from the document as they are drawn.
///
/// <b>Keys.</b> A row's position is its place in the tree with everything expanded (see
/// <see cref="StructNode.Position"/>), so keys are unique and in row order whatever is collapsed,
/// and the row a key names never changes while the mapping does not. The scroll range is the
/// mapping's bytes, as a document tree's is its document's.
///
/// By default the struct and the structs in it are open, and arrays - which may be any length -
/// closed. When the mapping moves - following the caret - only <see cref="Start"/> changes: the
/// rows, their keys and what is open stay, so the surface keeps its place and lays the rows out
/// again (<c>TreeSurface.InvalidateRows</c>) rather than starting over with a new tree.
/// Single-threaded, on the UI thread, like the surface.
/// </summary>
public sealed class StructTree : ITreeRowSource
{
    private readonly Dictionary<long, bool> overrides = new();

    /// <param name="nodes">The mapped struct's rows.</param>
    /// <param name="bytes">The document, read for each row drawn - never released here.</param>
    /// <param name="start">Where the mapping starts in the document.</param>
    public StructTree(StructNodes nodes, IByteSource bytes, long start, ByteOrder order, StructTarget target)
    {
        Nodes = nodes;
        Bytes = bytes;
        Start = start;
        Order = order;
        Target = target;
        Painter = new StructTreePainter(this);
        OffsetGutter = new StructOffsetGutter(this);
        Gutters = [OffsetGutter];
    }

    public StructNodes Nodes { get; }

    public IByteSource Bytes { get; }

    /// <summary>Where the mapping starts in the document; moved as the caret moves.</summary>
    public long Start { get; set; }

    /// <summary>The byte order values are read in - the Values tab's, so changing it rereads the
    /// rows but changes none of them.</summary>
    public ByteOrder Order { get; set; }

    public StructTarget Target { get; }

    public ITreeRowPainter Painter { get; }

    public IReadOnlyList<ITreeGutter> Gutters { get; }

    /// <summary>The column of each row's offset in the struct.</summary>
    public StructOffsetGutter OffsetGutter { get; }

    public event EventHandler? Grew;

    public event EventHandler? Closing;

    /// <summary>Tells a surface showing the rows that more of the document has arrived.</summary>
    public void NotifyGrew() => Grew?.Invoke(this, EventArgs.Empty);

    /// <summary>Tells a surface showing the rows to let go: the document is about to be released.</summary>
    public void Close() => Closing?.Invoke(this, EventArgs.Empty);

    public ITreeRowCursor NewCursor() => new StructTreeCursor(this);

    public bool IsExpanded(in StructNode node)
    {
        if (!node.IsContainer)
            return false;
        if (this.overrides.TryGetValue(node.Position, out bool expanded))
            return expanded;
        return node.Kind == StructNodeKind.Root || (!node.Value.IsArray && node.Kind == StructNodeKind.Field);
    }

    public void Toggle(in TreeRow row)
    {
        if (row.Detail is StructNode node)
            Set(node, !IsExpanded(node));
    }

    public void SetExpanded(in TreeRow row, bool expanded)
    {
        if (row.Detail is StructNode node)
            Set(node, expanded);
    }

    private void Set(in StructNode node, bool expanded)
    {
        if (node.IsContainer)
            this.overrides[node.Position] = expanded;
    }

    public bool ExpandDeep(in TreeRow row, int rowBudget)
    {
        if (row.Detail is not StructNode node)
            return true;

        int budget = rowBudget;
        return ExpandDeep(node, ref budget);
    }

    private bool ExpandDeep(in StructNode node, ref int budget)
    {
        if (!node.IsContainer)
            return true;

        Set(node, true);
        long count = Nodes.ChildCount(node);
        for (long i = 0; i < count; i++)
        {
            if (--budget < 0)
                return false;
            if (!ExpandDeep(Nodes.Child(node, i), ref budget))
                return false;
        }

        return true;
    }

    public void CollapseDeep(in TreeRow row)
    {
        if (row.Detail is not StructNode node)
            return;

        long first = node.Position, last = node.Position + node.Rows;
        var inside = new List<long>();
        foreach (long position in this.overrides.Keys)
        {
            if (position > first && position < last)
                inside.Add(position);
        }

        foreach (long position in inside)
            this.overrides.Remove(position);
        Set(node, false);
    }

    public bool Hides(in TreeRow row, long position)
        => row.Detail is StructNode node && !IsExpanded(node) && position > node.Position && position < node.Position + node.Rows;

    public long ScrollLength => Math.Max(1, Nodes.Size);

    public long ScrollPosition(in TreeRow row) => row.Detail is StructNode node ? node.Offset : 0;

    public void SeekScrollPosition(ITreeRowCursor cursor, long position)
    {
        if (cursor is StructTreeCursor structCursor)
            structCursor.SeekOffset(position);
        else
            cursor.SeekTo(position);
    }

    public bool IsComplete => true;

    /// <summary>The value <paramref name="node"/> reads as.</summary>
    public StructText ValueOf(in StructNode node) => StructValueText.Read(Bytes, Start, node, Order, Target);

    /// <summary>The row <paramref name="node"/> is, for the surface.</summary>
    internal TreeRow RowOf(in StructNode node, int depth, long parentPosition)
    {
        bool container = node.IsContainer;
        var treeNode = new TreeNode(node.Position, node.Position, node.Position + node.Rows, container, (byte)node.Kind);
        return new TreeRow(
            container ? TreeRowShape.Open : TreeRowShape.Leaf,
            treeNode,
            node.Position,
            depth,
            node.Ordinal,
            0,
            parentPosition,
            container && IsExpanded(node),
            node);
    }
}

/// <summary>
/// Walks a <see cref="StructTree"/>'s visible rows: a path from the root to the row it stands on,
/// each step a child of the one before, so a move is a step along a parent's children or into or
/// out of one.
/// </summary>
internal sealed class StructTreeCursor(StructTree tree) : ITreeRowCursor
{
    private List<StructNode> path = [];

    public TreeRow Current
    {
        get
        {
            var node = this.path[^1];
            long parent = this.path.Count > 1 ? this.path[^2].Position : -1;
            return tree.RowOf(node, this.path.Count - 1, parent);
        }
    }

    public IEnumerable<TreeRow> Ancestors
    {
        get
        {
            for (int i = 0; i < this.path.Count - 1; i++)
                yield return tree.RowOf(this.path[i], i, i > 0 ? this.path[i - 1].Position : -1);
        }
    }

    private StructNodes Nodes => tree.Nodes;

    public bool MoveToStart()
    {
        this.path = [Nodes.Root];
        return true;
    }

    public bool MoveToEnd()
    {
        this.path = [Nodes.Root];
        DescendToLast();
        return true;
    }

    public bool MoveNext()
    {
        if (this.path.Count == 0)
            return false;

        var node = this.path[^1];
        if (tree.IsExpanded(node) && Nodes.ChildCount(node) > 0)
        {
            this.path.Add(Nodes.Child(node, 0));
            return true;
        }

        for (int level = this.path.Count - 1; level > 0; level--)
        {
            var parent = this.path[level - 1];
            long next = this.path[level].Ordinal + 1;
            if (next < Nodes.ChildCount(parent))
            {
                this.path.RemoveRange(level, this.path.Count - level);
                this.path.Add(Nodes.Child(parent, next));
                return true;
            }
        }

        return false;
    }

    public bool MovePrevious()
    {
        if (this.path.Count < 2)
            return false;

        long ordinal = this.path[^1].Ordinal;
        this.path.RemoveAt(this.path.Count - 1);
        if (ordinal == 0)
            return true;

        this.path.Add(Nodes.Child(this.path[^1], ordinal - 1));
        DescendToLast();
        return true;
    }

    /// <summary>Down to the last visible row under the current one.</summary>
    private void DescendToLast()
    {
        while (true)
        {
            var node = this.path[^1];
            long count = Nodes.ChildCount(node);
            if (!tree.IsExpanded(node) || count == 0)
                return;
            this.path.Add(Nodes.Child(node, count - 1));
        }
    }

    public bool SeekTo(long position)
    {
        this.path = [Nodes.Root];
        while (true)
        {
            var node = this.path[^1];
            if (position <= node.Position || !tree.IsExpanded(node) || Nodes.ChildCount(node) == 0)
                return true;
            if (position >= node.Position + node.Rows)
            {
                if (this.path.Count == 1)
                    DescendToLast();
                return true;
            }

            this.path.Add(Nodes.ChildHolding(node, position));
        }
    }

    /// <summary>Stands on the deepest visible row whose bytes hold <paramref name="offset"/>, from
    /// the mapping's start.</summary>
    public void SeekOffset(long offset)
    {
        this.path = [Nodes.Root];
        while (true)
        {
            var node = this.path[^1];
            if (!tree.IsExpanded(node) || Nodes.ChildAtOffset(node, offset) is not { } child)
                return;
            this.path.Add(child);
        }
    }

    public ITreeRowCursor Clone() => new StructTreeCursor(tree) { path = [.. this.path] };
}

/// <summary>
/// What a struct tree row says: <c>name: value</c>, the value coloured by what it is, then the
/// field's type as a note - <c>int</c>, <c>char[16]</c>, <c>unsigned int : 3</c>. Padding rows say
/// how many bytes they are.
/// </summary>
internal sealed class StructTreePainter(StructTree tree) : ITreeRowPainter
{
    public void AppendRuns(in TreeRow row, List<TreeRun> runs)
    {
        if (row.Detail is not StructNode node)
            return;

        var value = tree.ValueOf(node);
        if (node.Kind == StructNodeKind.Padding)
        {
            runs.Add(new TreeRun("padding", TreeRunStyle.Comment));
            runs.Add(new TreeRun(value.Text, TreeRunStyle.Note));
            return;
        }

        runs.Add(new TreeRun(LabelOf(node) + ": ", TreeRunStyle.Name));
        runs.Add(new TreeRun(value.Text, value.Kind switch
        {
            StructTextKind.Number => TreeRunStyle.Number,
            StructTextKind.Text => TreeRunStyle.String,
            StructTextKind.Keyword => TreeRunStyle.Keyword,
            StructTextKind.Missing => TreeRunStyle.Comment,
            _ => TreeRunStyle.Plain,
        }));
        runs.Add(new TreeRun(TypeText(node.Value), TreeRunStyle.Note));
    }

    private static string LabelOf(in StructNode node)
    {
        if (node.IsBase)
            return $"(base {node.Label})";
        if (!node.IsAnonymous)
            return node.Label;
        if (node.Value.BitWidth is not null)
            return "(unnamed bits)";
        return node.Value.Struct?.Kind == StructKind.Union ? "(union)" : "(struct)";
    }

    /// <summary>The type as declared: its name, the dimensions still to come, a bitfield's width.</summary>
    internal static string TypeText(in StructValue value)
    {
        var text = new StringBuilder(value.TypeName);
        for (int d = value.Dimension; d < value.Lengths.Count; d++)
            text.Append('[').Append(value.Lengths[d]).Append(']');
        if (value.BitWidth is { } width)
            text.Append(" : ").Append(width);
        return text.ToString();
    }
}
