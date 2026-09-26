using System;
using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;

namespace Argonaut.Ui.Tree;

/// <summary>
/// What <see cref="TreeSurface"/> draws: rows it walks with a cursor, what they say, how they
/// open and close, and where a row is for the scrollbar. <see cref="TreeDocument"/> is one over a
/// document's bytes; a comparison of two documents is another.
///
/// The scroll model is estimated, like every tree's: a row has a scroll position between 0 and
/// <see cref="ScrollLength"/> - for a document, its byte offset - and the surface takes the top
/// row's as the scrollbar's value.
/// </summary>
public interface ITreeRowSource
{
    ITreeRowPainter Painter { get; }

    IReadOnlyList<ITreeGutter> Gutters { get; }

    /// <summary>Raised when there is more to show, so the rows on screen and the scroll range can
    /// catch up.</summary>
    event EventHandler? Grew;

    /// <summary>Raised when what the rows are read from is about to be released: a surface
    /// showing them lets go at once.</summary>
    event EventHandler? Closing;

    ITreeRowCursor NewCursor();

    /// <summary>Expands or collapses <paramref name="row"/>'s container.</summary>
    void Toggle(in TreeRow row);

    void SetExpanded(in TreeRow row, bool expanded);

    /// <summary>Opens <paramref name="row"/> and everything beneath it, stopping after
    /// <paramref name="rowBudget"/> rows; false when it stopped.</summary>
    bool ExpandDeep(in TreeRow row, int rowBudget);

    /// <summary>Closes <paramref name="row"/> and forgets what was opened beneath it, so opening
    /// it again shows the default.</summary>
    void CollapseDeep(in TreeRow row);

    /// <summary>Whether the collapsed <paramref name="row"/> hides the row at
    /// <paramref name="position"/> - what a reveal opens.</summary>
    bool Hides(in TreeRow row, long position);

    /// <summary>The scroll range, in the same units as <see cref="ScrollPosition"/>.</summary>
    long ScrollLength { get; }

    /// <summary>Where <paramref name="row"/> is in the scroll range.</summary>
    long ScrollPosition(in TreeRow row);

    /// <summary>Stands <paramref name="cursor"/> on the row at <paramref name="position"/> in the
    /// scroll range - no further than can be reached cheaply while the rows are still being
    /// worked out.</summary>
    void SeekScrollPosition(ITreeRowCursor cursor, long position);

    /// <summary>True once the last row is known, so going to the end can stand on it.</summary>
    bool IsComplete { get; }
}
