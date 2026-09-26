namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// One recorded container of a <see cref="SparseContainerIndex"/>: a node whose bytes reached the
/// index's promotion size. Offsets are in the indexed source's coordinates.
/// </summary>
/// <param name="Start">Offset of the container's first byte - its opening bracket or start tag.</param>
/// <param name="End">Offset one past its last byte, or -1 while it is still open.</param>
/// <param name="Parent">The enclosing recorded container, or -1 at the top. A recorded container's
/// parent is always recorded, since it is larger.</param>
/// <param name="Depth">Nesting depth in the document, 0 for a top-level container.</param>
/// <param name="OrdinalInParent">Which child of its parent this is, from 0; 0 at the top.</param>
/// <param name="ChildCount">Number of children, or -1 while it is still open.</param>
/// <param name="FormatKind">A byte only the format interprets - object or array, element kind.</param>
public readonly record struct TreeContainer(
    long Start,
    long End,
    int Parent,
    int Depth,
    long OrdinalInParent,
    long ChildCount,
    byte FormatKind)
{
    public bool IsOpen => End < 0;
}
