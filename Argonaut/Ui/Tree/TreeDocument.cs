using System;
using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Ui.Tree;

/// <summary>
/// A tree-shaped document as <see cref="TreeSurface"/> shows it: the sparse index and the format's
/// reader to walk it, the expansion, the format's painter and gutters, and how many bytes there
/// are so far. Built by a format's view model; the surface reads it and changes nothing but the
/// expansion.
///
/// Rows are positioned by byte offset, and so is the scrollbar: a row's scroll position is where
/// its text starts in the document.
/// </summary>
public sealed class TreeDocument(
    SparseContainerIndex index,
    ITreeFormatReader reader,
    ITreeRowPainter painter,
    TreeExpandState expand,
    Func<long> availableLength,
    IReadOnlyList<ITreeGutter>? gutters = null) : ITreeRowSource
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

    /// <summary>Raised when the bytes behind this document are about to be released: a surface
    /// showing it lets go at once, since drawing another row would read a released mapping.</summary>
    public event EventHandler? Closing;

    /// <summary>Called by the owner before it releases the bytes.</summary>
    public void Close() => Closing?.Invoke(this, EventArgs.Empty);

    public TreeCursor NewCursor() => new(Index, Reader, Expand);

    ITreeRowCursor ITreeRowSource.NewCursor() => NewCursor();

    public void Toggle(in TreeRow row) => Expand.Toggle(row.Node.ValueStart);

    public void SetExpanded(in TreeRow row, bool expanded) => Expand.SetExpanded(row.Node.ValueStart, row.Depth, expanded);

    public bool ExpandDeep(in TreeRow row, int rowBudget)
    {
        // Walk the subtree as if everything were open, opening each container for real.
        var everything = new TreeCursor(Index, Reader, new TreeExpandState(int.MaxValue));
        everything.SeekTo(row.Start);
        Expand.SetExpanded(row.Node.ValueStart, row.Depth, true);

        int budget = rowBudget;
        while (budget-- > 0 && everything.MoveNext() && everything.Current.Depth > row.Depth)
        {
            if (everything.Current is { Shape: TreeRowShape.Open } open)
                Expand.SetExpanded(open.Node.ValueStart, open.Depth, true);
        }

        return budget >= 0;
    }

    public void CollapseDeep(in TreeRow row)
    {
        long start = row.Node.ValueStart;
        Expand.SetExpanded(start, row.Depth, false);
        Expand.ResetWithin(start, ContainerEndOrMax(start));
    }

    /// <summary>A collapsed container hides every offset past its opening and before its end.</summary>
    public bool Hides(in TreeRow row, long position)
        => position >= Reader.FirstChildPosition(row.Node.ValueStart) && position < ContainerEndOrMax(row.Node.ValueStart);

    public long ScrollLength => AvailableLength;

    public long ScrollPosition(in TreeRow row) => row.Start;

    /// <summary>
    /// Never past what the index covers. Beyond it a seek has no resume point nearer than the
    /// last one indexed, and would read every sibling in between - gigabytes of them, on the UI
    /// thread, for each move of the thumb. Until the index gets there, the view stops at its edge.
    /// </summary>
    public void SeekScrollPosition(ITreeRowCursor cursor, long position)
    {
        if (!Index.IsComplete)
            position = Math.Min(position, Index.ScannedTo);

        if (position <= 0)
            cursor.MoveToStart();
        else
            cursor.SeekTo(position);
    }

    public bool IsComplete => Index.IsComplete;

    private long ContainerEndOrMax(long containerStart)
    {
        int record = Index.FindContainerStartingAt(containerStart);
        return record >= 0 && Index.GetContainer(record).End is var end and >= 0
            ? end
            : Reader.SkipValue(containerStart);
    }
}
