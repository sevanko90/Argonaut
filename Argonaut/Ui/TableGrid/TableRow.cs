using System.Collections.Generic;

namespace Argonaut.Ui.TableGrid;

/// <summary>One displayed data row: 1-based row number plus its cells' text. Carries no
/// geometry - the grid owns column widths, so a realized row never goes stale when one is
/// resized or the content font changes.</summary>
public sealed class TableRow
{
    public TableRow(int rowNumber, IReadOnlyList<TableCell> cells)
    {
        RowNumber = rowNumber;
        Cells = cells;
    }

    public int RowNumber { get; }

    public IReadOnlyList<TableCell> Cells { get; }
}
