namespace Argonaut.Engine.Indexing.Trees;

/// <summary>A node as a format reader finds it inside a container.</summary>
/// <param name="RowStart">Where the node's row begins in the bytes: its value, or the name that
/// precedes it (a JSON member's key).</param>
/// <param name="ValueStart">Where the value itself begins - a container's opening byte. A row's
/// identity, since it is stable while the rows around it expand and collapse.</param>
/// <param name="ValueEnd">One past a leaf's last byte, or -1 for a container, whose end the
/// cursor finds from the index or <see cref="ITreeFormatReader.SkipValue"/>.</param>
/// <param name="IsContainer">Whether the node has children.</param>
/// <param name="FormatKind">A byte only the format interprets.</param>
public readonly record struct TreeNode(long RowStart, long ValueStart, long ValueEnd, bool IsContainer, byte FormatKind);

/// <summary>
/// What a format supplies for <see cref="TreeCursor"/> to walk its documents: how to find the next
/// child in a container, where a container's children begin, and how to get past a value without
/// reading it. Everything else - stacks, ordinals, expansion, stepping backward, seeking through
/// the sparse index - is the cursor's and shared by every format.
///
/// Positions are always between nodes: after a container's opening or after a child's end. A
/// reader skips whatever joins nodes in its syntax (separators, whitespace, comments).
/// </summary>
public interface ITreeFormatReader
{
    /// <summary>The format kind of the document level - the pseudo-container holding the
    /// top-level values - distinct from every real container's kind.</summary>
    byte DocumentKind { get; }

    /// <summary>
    /// Reads the next child of a container of kind <paramref name="containerKind"/> from
    /// <paramref name="position"/>. On true, <paramref name="child"/> is set and
    /// <paramref name="position"/> is left at the child's value start. On false the container
    /// closes: <paramref name="closeStart"/> is where its closing row begins - for the document
    /// level, the end of the data read so far.
    /// </summary>
    bool TryReadChild(byte containerKind, ref long position, out TreeNode child, out long closeStart);

    /// <summary>Where a container's first child may begin, given the container's first byte.</summary>
    long FirstChildPosition(long containerStart);

    /// <summary>One past the last byte of the container value starting at
    /// <paramref name="containerStart"/>.</summary>
    long SkipValue(long containerStart);

    /// <summary>Where a container's closing row begins, given its start and end.</summary>
    long CloseStart(long containerStart, long containerEnd);
}
