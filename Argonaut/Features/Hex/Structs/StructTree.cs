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
/// The root - the struct, or the array of them - is not a row: the title bar above the tree names
/// it, and its fields are the top rows. By default the structs in it are open, and arrays - which
/// may be any length - closed. When the mapping moves - following the caret - only <see cref="Start"/> changes: the
/// rows, their keys and what is open stay, so the surface keeps its place and lays the rows out
/// again (<c>TreeSurface.InvalidateRows</c>) rather than starting over with a new tree.
/// Single-threaded, on the UI thread, like the surface.
/// </summary>
public sealed class StructTree : ITreeRowSource
{
    private readonly Dictionary<long, bool> overrides = new();

    // The arrays switched between text and items, by position, where the switch differs from what
    // they read as by default.
    private readonly Dictionary<long, bool> textChoices = new();

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
        if (node.Kind == StructNodeKind.Root)
            return true;
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
        if (node.IsContainer && node.Kind != StructNodeKind.Root)
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
    public StructText ValueOf(in StructNode node) => StructValueText.Read(Bytes, Start, node, Order, Target, ReadsAsText(node));

    /// <summary>Whether <paramref name="node"/> is an array that can be switched between text and
    /// its items.</summary>
    public bool CanReadAsText(in StructNode node) => StructValueText.CanReadAsText(node);

    /// <summary>
    /// Whether <paramref name="node"/> reads as text: as switched, or by default - a character
    /// array always, a byte array when its bytes here look like text. The default is worked out
    /// where the struct is, so a switch is kept as the caret moves but a default follows the bytes.
    /// </summary>
    public bool ReadsAsText(in StructNode node)
        => this.textChoices.TryGetValue(node.Position, out bool chosen) ? chosen && CanReadAsText(node) : StructValueText.ReadsAsTextByDefault(Bytes, Start, node);

    /// <summary>Switches <paramref name="node"/> between text and its items.</summary>
    public void ToggleText(in StructNode node)
    {
        if (CanReadAsText(node))
            this.textChoices[node.Position] = !ReadsAsText(node);
    }

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
/// out of one. The root itself is never a row - the title bar above the tree names the struct,
/// and it is always open - so the rows start at its fields (or, for an array, its elements), at
/// depth 0.
/// </summary>
internal sealed class StructTreeCursor(StructTree tree) : ITreeRowCursor
{
    private List<StructNode> path = [];

    public TreeRow Current
    {
        get
        {
            var node = this.path[^1];
            long parent = this.path.Count > 2 ? this.path[^2].Position : -1;
            return tree.RowOf(node, this.path.Count - 2, parent);
        }
    }

    public IEnumerable<TreeRow> Ancestors
    {
        get
        {
            for (int i = 1; i < this.path.Count - 1; i++)
                yield return tree.RowOf(this.path[i], i - 1, i > 1 ? this.path[i - 1].Position : -1);
        }
    }

    private StructNodes Nodes => tree.Nodes;

    public bool MoveToStart()
    {
        if (Nodes.ChildCount(Nodes.Root) == 0)
            return false;

        this.path = [Nodes.Root, Nodes.Child(Nodes.Root, 0)];
        return true;
    }

    public bool MoveToEnd()
    {
        long count = Nodes.ChildCount(Nodes.Root);
        if (count == 0)
            return false;

        this.path = [Nodes.Root, Nodes.Child(Nodes.Root, count - 1)];
        DescendToLast();
        return true;
    }

    public bool MoveNext()
    {
        if (this.path.Count < 2)
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
        if (ordinal == 0 && this.path.Count == 2)
            return false;

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
        var root = Nodes.Root;
        if (Nodes.ChildCount(root) == 0)
            return false;
        if (position <= root.Position)
            return MoveToStart();
        if (position >= root.Position + root.Rows)
            return MoveToEnd();

        this.path = [root, Nodes.ChildHolding(root, position)];
        while (true)
        {
            var node = this.path[^1];
            if (position <= node.Position || !tree.IsExpanded(node) || Nodes.ChildCount(node) == 0)
                return true;
            this.path.Add(Nodes.ChildHolding(node, position));
        }
    }

    /// <summary>Stands on the deepest visible row whose bytes hold <paramref name="offset"/>, from
    /// the mapping's start; the first row when none does.</summary>
    public void SeekOffset(long offset)
    {
        this.path = [Nodes.Root];
        while (true)
        {
            var node = this.path[^1];
            if ((this.path.Count > 1 && !tree.IsExpanded(node)) || Nodes.ChildAtOffset(node, offset) is not { } child)
                break;
            this.path.Add(child);
        }

        if (this.path.Count < 2)
            MoveToStart();
    }

    public ITreeRowCursor Clone() => new StructTreeCursor(tree) { path = [.. this.path] };
}

/// <summary>
/// What a struct tree row says, in three columns lined up with its siblings' as a header reads:
/// the name, the type (<c>int</c>, <c>char[16]</c>, <c>unsigned int : 3</c>) muted, and the
/// value coloured by what it is. Padding rows say how many bytes they are. Hovering a row says
/// how its type is stored (<see cref="StructStorageNotes"/>).
/// </summary>
internal sealed class StructTreePainter(StructTree tree) : ITreeRowPainter
{
    /// <summary>The space between columns.</summary>
    private const int Gap = 2;

    public void AppendRuns(in TreeRow row, List<TreeRun> runs)
    {
        if (row.Detail is not StructNode node)
            return;

        var (labelWidth, typeWidth) = tree.Nodes.ColumnsOf(node.Parent);
        var value = tree.ValueOf(node);
        bool padding = node.Kind == StructNodeKind.Padding;

        runs.Add(new TreeRun(node.DisplayLabel.PadRight(labelWidth + Gap), padding ? TreeRunStyle.Comment : TreeRunStyle.Name));
        runs.Add(new TreeRun(node.TypeColumn.PadRight(typeWidth + Gap), TreeRunStyle.Hint));
        runs.Add(new TreeRun(value.Text, padding ? TreeRunStyle.Comment : value.Kind switch
        {
            StructTextKind.Number => TreeRunStyle.Number,
            StructTextKind.Text => TreeRunStyle.String,
            StructTextKind.Keyword => TreeRunStyle.Keyword,
            StructTextKind.Missing => TreeRunStyle.Comment,
            _ => TreeRunStyle.Plain,
        }));

        if (tree.CanReadAsText(node))
            runs.Add(new TreeRun(tree.ReadsAsText(node) ? "as items" : "as text", TreeRunStyle.Action, new StructTextSwitch(node)));
    }

    /// <summary>How the row's type is stored, in the definition's language and the target.</summary>
    public object? ToolTipFor(in TreeRow row)
        => row.Detail is StructNode node ? StructStorageNotes.For(node, tree.Nodes.Struct.Language, tree.Target, tree.Order) : null;
}

/// <summary>The link of a row's "as text" / "as items" chip: the array it switches.</summary>
public sealed record StructTextSwitch(StructNode Node);
