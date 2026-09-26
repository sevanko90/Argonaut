namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// Where a format's parser should start to reach a target inside a
/// <see cref="SparseContainerIndex"/>: inside <see cref="Container"/>, at the child numbered
/// <see cref="Ordinal"/>. The parser's ancestor stack is the container's parent chain.
/// </summary>
/// <param name="Container">The recorded container to resume in, or -1 for the top of the
/// document, where no recorded container encloses the target.</param>
/// <param name="Offset">Where to start reading: a child's start (or the whitespace before it), or
/// when <paramref name="AtOpen"/> the container's own first byte.</param>
/// <param name="Ordinal">The ordinal of the child at <paramref name="Offset"/>; 0 when
/// <paramref name="AtOpen"/>.</param>
/// <param name="AtOpen">True when the parser must first read the container's opening - its
/// bracket or start tag - before its first child. A format knows how long that is; the index
/// does not.</param>
public readonly record struct TreeResumePoint(int Container, long Offset, long Ordinal, bool AtOpen);
