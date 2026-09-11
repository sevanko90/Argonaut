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
}
