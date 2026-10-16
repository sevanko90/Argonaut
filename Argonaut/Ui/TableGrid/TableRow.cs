using System.Collections.Generic;

namespace Argonaut.Ui.TableGrid;

/// <summary>One displayed data row: 1-based row number plus its cells' text, and where it is in
/// its document for a gutter to label it by. Carries no geometry - the grid owns column widths,
/// so a realized row never goes stale when one is resized or the content font changes.</summary>
public sealed class TableRow
{
    public TableRow(int rowNumber, IReadOnlyList<TableCell> cells)
        : this(rowNumber, cells, rowNumber, lineStart: 0)
    {
    }

    /// <param name="lineNumber">The document line the row is - which, under a header, is not its
    /// row number.</param>
    /// <param name="lineStart">Where that line starts in the document.</param>
    public TableRow(int rowNumber, IReadOnlyList<TableCell> cells, long lineNumber, long lineStart)
    {
        RowNumber = rowNumber;
        Cells = cells;
        LineNumber = lineNumber;
        LineStart = lineStart;
    }

    public int RowNumber { get; }

    public IReadOnlyList<TableCell> Cells { get; }

    /// <summary>The 1-based document line the row is.</summary>
    public long LineNumber { get; }

    /// <summary>Where <see cref="LineNumber"/> starts in the document, in bytes.</summary>
    public long LineStart { get; }
}

/// <summary>Where the find match find is on sits in a grid: the row and column, and which
/// occurrence of the term in that cell's text it is, 0 for the first.</summary>
public readonly record struct TableMatch(int Row, int Column, int Occurrence);
