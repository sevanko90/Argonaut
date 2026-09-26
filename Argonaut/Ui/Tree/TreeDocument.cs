using System;
using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Ui.Tree;

/// <summary>
/// A tree-shaped document as <see cref="TreeSurface"/> shows it: the sparse index and the format's
/// reader to walk it, the expansion, the format's painter and gutters, and how many bytes there
/// are so far. Built by a format's view model; the surface reads it and never changes it except
/// through <see cref="Expand"/>.
/// </summary>
public sealed class TreeDocument(
    SparseContainerIndex index,
    ITreeFormatReader reader,
    ITreeRowPainter painter,
    TreeExpandState expand,
    Func<long> availableLength,
    IReadOnlyList<ITreeGutter>? gutters = null)
{
    public SparseContainerIndex Index { get; } = index;

    public ITreeFormatReader Reader { get; } = reader;

    public ITreeRowPainter Painter { get; } = painter;

    public TreeExpandState Expand { get; } = expand;

    public IReadOnlyList<ITreeGutter> Gutters { get; } = gutters ?? Array.Empty<ITreeGutter>();

    /// <summary>Bytes readable so far - the scroll range is estimated from it.</summary>
    public long AvailableLength => availableLength();

    /// <summary>Raised when more of the document has arrived or been indexed, so the rows on
    /// screen and the scroll range can catch up.</summary>
    public event EventHandler? Grew;

    public void NotifyGrew() => Grew?.Invoke(this, EventArgs.Empty);

    public TreeCursor NewCursor() => new(Index, Reader, Expand);
}
