using System.Collections.Generic;

namespace Argonaut.Engine.Indexing.Trees;

/// <summary>
/// Stands on one display row of a tree and steps to its neighbours - what a view drawing a tree
/// walks. <see cref="TreeCursor"/> is the one over a document's bytes; a view that merges or
/// derives rows supplies its own, and the view draws either the same way.
///
/// Positions are the rows' <see cref="TreeRow.Start"/>: seeking one lands on the row showing it -
/// a collapsed ancestor's row if it is hidden, the next row if it falls between rows.
/// </summary>
public interface ITreeRowCursor
{
    /// <summary>The row the cursor stands on. Meaningful once a move or seek has returned
    /// true.</summary>
    TreeRow Current { get; }

    /// <summary>The open rows of the containers enclosing <see cref="Current"/>, outermost
    /// first.</summary>
    IEnumerable<TreeRow> Ancestors { get; }

    /// <summary>Stands on the first row; false when there are none.</summary>
    bool MoveToStart();

    /// <summary>Stands on the last row; false when there are none.</summary>
    bool MoveToEnd();

    /// <summary>To the next row; false, without moving, at the last.</summary>
    bool MoveNext();

    /// <summary>To the previous row; false, without moving, at the first.</summary>
    bool MovePrevious();

    /// <summary>Stands on the row showing <paramref name="position"/>; false only when there
    /// are no rows.</summary>
    bool SeekTo(long position);

    /// <summary>A second cursor on the same row that moves independently.</summary>
    ITreeRowCursor Clone();
}
