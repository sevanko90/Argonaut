namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// A place a parse can resume inside a recorded container: the start of one of its children
/// (possibly preceded by whitespace), with that child's ordinal.
/// </summary>
/// <param name="Offset">Where the child begins, or the whitespace before it.</param>
/// <param name="Ordinal">The child's position among its container's children, from 0.</param>
/// <param name="Container">The recorded container the child belongs to.</param>
public readonly record struct TreeCheckpoint(long Offset, long Ordinal, int Container);
