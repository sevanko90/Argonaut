namespace Argonaut.Features.Csv;

/// <summary>
/// A grid body that can say how wide a column's content actually is - answered from the rows it
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
    /// The longest cell text, in characters, that <paramref name="columnIndex"/> has among the
    /// currently cached rows - 0 when nothing is cached or the column has no cells.
    /// </summary>
    int LongestRealizedText(int columnIndex);
}
