using System.Collections.Generic;

namespace Argonaut.Ui.TableGrid;

/// <summary>
/// A grid body that can say what a column's content actually is - answered from the rows it
/// has already realized, never by reading the file.
///
/// That restriction is the whole design: fitting a column to its true widest value would mean a
/// full scan of a multi-GB document for one double-click. The realized-row cache is a free,
/// bounded sample of what the user has actually been looking at, which is the right answer for a
/// gesture that means "show me what is in this column".
/// </summary>
public interface IColumnFitSource
{
    /// <summary>
    /// The text of every cell <paramref name="columnIndex"/> has among the currently cached rows -
    /// none when nothing is cached or the column has no cells. The grid measures them itself,
    /// since in a proportional font the longest text is not necessarily the widest.
    /// </summary>
    IEnumerable<string> RealizedTexts(int columnIndex);
}
