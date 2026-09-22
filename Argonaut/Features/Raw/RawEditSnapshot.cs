using System.Collections.Generic;

namespace Argonaut.Features.Raw;

/// <summary>One run of bytes in the piece table, flattened so it can be displayed.</summary>
/// <param name="FromOriginal">True for a run of the file's own bytes, false for typed or pasted
/// ones living in scratch.</param>
/// <param name="ScratchChunk">Which scratch chunk, or -1 for the original.</param>
/// <param name="BufferOffset">Where the run starts inside whichever buffer holds it.</param>
/// <param name="LogicalStart">Where the run starts in the document the user sees.</param>
public readonly record struct RawPieceSnapshot(
    int Index,
    bool FromOriginal,
    int ScratchChunk,
    long BufferOffset,
    long Length,
    long LogicalStart);

/// <summary>
/// One dirty span of <see cref="RawEditedRowIndex"/>, flattened so it can be displayed.
///
/// Both halves of each delta are carried: what this span did on its own, and the running total
/// of every span before it. The second is what displaces the untouched rows that follow, so
/// seeing them apart is the difference between a readable picture and a list of numbers.
/// </summary>
/// <param name="OriginalStartRow">First original row the span replaced.</param>
/// <param name="OriginalRowsEnd">The original row after the last one it replaced.</param>
/// <param name="LinesHeld">Line records the span holds - what the budget counts.</param>
public readonly record struct RawSpanSnapshot(
    int Index,
    int OriginalStartRow,
    int OriginalRowsEnd,
    int StartRow,
    long StartOffset,
    long EndOffset,
    int RowsHeld,
    int LinesHeld,
    int FirstLineNumber,
    long ByteDelta,
    int RowDelta,
    int LineDelta,
    long ByteDeltaBefore,
    int RowDeltaBefore,
    int LineDeltaBefore);

/// <summary>
/// What the raw editor's internals look like at one instant: the piece table, the dirty spans
/// over it, how full the row index's budget is, and where the caret sits.
///
/// It exists because none of that is visible from the document itself. A piece table is the whole
/// reason a multi-GB file can be edited and the whole reason nothing about the edit is where a
/// reader would expect it to be, and a dirty span is a region of the row index rather than
/// anything drawn - so the only way to watch either behave is to ask them.
///
/// A snapshot copies; it holds no reference to the structures it describes, so it stays readable
/// after the document it came from has moved on. Building one is O(spans + pieces shown) and
/// reads no bytes, which is what makes it safe to take on every keystroke.
/// </summary>
public sealed record RawEditSnapshot(
    long DocumentLength,
    long OriginalLength,
    bool IsUnedited,
    int PieceCount,
    int ScratchChunkCount,
    long ScratchBytesUsed,
    IReadOnlyList<RawPieceSnapshot> Pieces,
    int PiecesOmitted,
    int RowCount,
    int OriginalRowCount,
    int SpanCount,
    int TotalDerivedRows,
    int HeldLines,
    int MaxHeldLines,
    long BytesScannedInLastEdit,
    int LinesRebuiltInLastEdit,
    bool NeedsRebuild,
    IReadOnlyList<RawSpanSnapshot> Spans,
    int UndoDepth,
    bool CanUndo,
    bool CanRedo,
    long CaretOffset,
    long SelectionStart,
    long SelectionEnd)
{
    /// <summary>
    /// How much of the row index's budget the spans have taken, 0 to 1. The budget counts
    /// line records rather than rows: a row inside a span is arithmetic, and costs nothing to hold.
    /// </summary>
    public double BudgetUsed => MaxHeldLines == 0 ? 0 : (double)HeldLines / MaxHeldLines;

    /// <summary>Bytes the document has gained or lost against the file on disk.</summary>
    public long ByteDelta => DocumentLength - OriginalLength;

    /// <summary>Rows the document has gained or lost against the file on disk.</summary>
    public int RowDelta => RowCount - OriginalRowCount;
}
