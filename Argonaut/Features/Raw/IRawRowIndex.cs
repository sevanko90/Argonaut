namespace Argonaut.Features.Raw;

/// <summary>
/// The row questions a caret needs answered, asked of either the index over an unedited file
/// (<see cref="RawSegmentIndex"/>) or the one layered over an edited document
/// (<see cref="RawEditedRowIndex"/>).
///
/// The seam exists so caret movement, selection and rendering are written once. They ask where a
/// row starts and which row holds an offset; neither answer changes shape when the document
/// becomes editable, only where it comes from.
/// </summary>
public interface IRawRowIndex
{
    /// <summary>Rows currently available. Grows during a background scan.</summary>
    int RowCount { get; }

    /// <summary>Byte range, wrap state and line number of one display row.</summary>
    RawRowInfo GetRowInfo(int rowIndex);

    /// <summary>The row containing <paramref name="offset"/>, or null when it is not covered.</summary>
    int? RowForOffset(long offset);

    /// <summary>
    /// The line <paramref name="rowIndex"/> sits in, counting from 1 - including for a
    /// continuation row, where <see cref="GetRowInfo"/> reports null because a blank gutter is
    /// what a wrapped line should look like.
    ///
    /// Separate from <see cref="RawRowInfo.LineNumber"/> precisely because the two want opposite
    /// answers for the same row: the gutter wants "nothing to draw here", a caret readout wants
    /// "line 54". Nothing is stored for this - the line number falls out of the walk
    /// <see cref="GetRowInfo"/> already does from the row's anchor.
    /// </summary>
    int? LineContaining(int rowIndex);
}
