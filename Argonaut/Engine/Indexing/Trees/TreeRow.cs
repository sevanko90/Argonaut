namespace Argonaut.Engine.Indexing.Trees;

/// <summary>What kind of row a <see cref="TreeRow"/> is.</summary>
public enum TreeRowShape : byte
{
    /// <summary>A container's own row - its opening when expanded, its summary when not.</summary>
    Open,

    /// <summary>An expanded container's closing row.</summary>
    Close,

    /// <summary>A value with no children.</summary>
    Leaf,
}

/// <summary>
/// One display row of a tree, as <see cref="TreeCursor"/> stands on it.
/// </summary>
/// <param name="Shape">Open, close or leaf.</param>
/// <param name="Node">The node the row belongs to; for a close row, the container it closes.</param>
/// <param name="Start">Where the row's text begins: the node's <see cref="TreeNode.RowStart"/>, or
/// for a close row the closing's start.</param>
/// <param name="Depth">Nesting depth, 0 for a top-level value.</param>
/// <param name="Ordinal">The node's position among its parent's children, from 0.</param>
/// <param name="ParentKind">The format kind of the container holding the node - the reader's
/// <see cref="ITreeFormatReader.DocumentKind"/> at the top.</param>
/// <param name="ParentStart">The value start of the container holding the node, or -1 at the
/// top - enough to walk up a row's ancestry one seek at a time.</param>
/// <param name="IsExpanded">For an open row, whether its children are shown.</param>
public readonly record struct TreeRow(
    TreeRowShape Shape,
    TreeNode Node,
    long Start,
    int Depth,
    long Ordinal,
    byte ParentKind,
    long ParentStart,
    bool IsExpanded)
{
    /// <summary>The row's identity: the node's value start, and whether it is the closing row.
    /// Two rows are the same row exactly when these match.</summary>
    public (long ValueStart, bool IsClose) Key => (Node.ValueStart, Shape == TreeRowShape.Close);
}
