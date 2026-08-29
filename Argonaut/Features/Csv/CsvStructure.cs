using System;
using System.Collections.Generic;

namespace Argonaut.Features.Csv;

/// <summary>A cell's display text plus its column's fixed width, so header and data cells can
/// bind directly without a per-item lookup into a shared array.</summary>
public readonly record struct CsvCell(string Text, double Width);

/// <summary>One column: display name plus the fixed pixel width its cells render at.</summary>
public readonly record struct CsvColumn(string Name, double Width);

/// <summary>
/// The shape of a CSV-style grid as one immutable object: the column names, their fixed pixel
/// widths, and the prebuilt header row. Constructed by whoever knows the data, consumed by
/// whoever renders it - this type discovers nothing.
///
/// Column *identity* cannot live here because it has three different sources: a CSV header
/// line, the property names a JSON array's elements share, or "Column 1..N" placeholders a
/// dropdown chose. All three end up as names plus a per-column maximum character count, which
/// is the whole input <see cref="FromMaxChars"/> takes - so each caller measures in whatever
/// currency is cheapest for its own data (a CsvFieldSpan's byte length; a JsonTokenInfo's) and
/// nothing in here ever sees text.
///
/// Widths are a character-count heuristic, not a text measurement, per CLAUDE.md's
/// low-allocation guidance: content that doesn't fit is handled by the view with ellipsis + a
/// tooltip, not by resizing. They come from a sample of the rows available at first paint and
/// are then fixed for this structure's lifetime - a *new* structure replaces it when the grid's
/// shape changes (see CsvRowCollection.SetStructure).
///
/// A sealed class rather than a struct: it holds arrays either way, it is allocated once per
/// shape change rather than per row, and an immutable class avoids the defensive-copy traps a
/// struct with array fields invites.
/// </summary>
public sealed class CsvStructure
{
    private const double CharWidthPx = 7.0;
    private const double CellPadding = 16.0;
    private const double MinColumnWidth = 60.0;
    private const double MaxColumnWidth = 320.0;

    private readonly CsvColumn[] columns;

    private CsvStructure(CsvColumn[] columns, CsvCell[] headerCells, double totalWidth)
    {
        this.columns = columns;
        HeaderCells = headerCells;
        TotalWidth = totalWidth;
    }

    public IReadOnlyList<CsvColumn> Columns => this.columns;

    public int ColumnCount => this.columns.Length;

    public double TotalWidth { get; }

    /// <summary>The header row, prebuilt in the same <see cref="CsvCell"/> shape the data rows
    /// use - so the sticky header and the body bind to identical items and cannot disagree
    /// about a column's width.</summary>
    public IReadOnlyList<CsvCell> HeaderCells { get; }

    /// <summary>Width for a column index, including ones beyond <see cref="ColumnCount"/> (a
    /// row with more fields than the header) - those fall back to the minimum column
    /// width.</summary>
    public double WidthFor(int columnIndex)
        => columnIndex >= 0 && columnIndex < this.columns.Length ? this.columns[columnIndex].Width : MinColumnWidth;

    /// <summary>
    /// The one factory: per-column pixel widths from a per-column maximum character count.
    /// <paramref name="maxChars"/> is indexed in parallel with <paramref name="names"/>; a
    /// column the caller had no counts for (a shorter span) is widthed from its name alone.
    /// Callers are expected to seed each count with the name's own length where the header
    /// must always fit.
    ///
    /// The formula saturates - the clamp hits its maximum at or past ~44 characters and its
    /// minimum at or below ~6 - which is why an over-counting measure (UTF-8 bytes rather than
    /// characters, a CSV field's length including its quotes) is harmless here.
    /// </summary>
    public static CsvStructure FromMaxChars(IReadOnlyList<string> names, ReadOnlySpan<int> maxChars)
    {
        var columns = new CsvColumn[names.Count];
        var headerCells = new CsvCell[names.Count];
        double total = 0;

        for (int c = 0; c < columns.Length; c++)
        {
            int chars = c < maxChars.Length ? maxChars[c] : names[c].Length;
            double width = Math.Clamp(chars * CharWidthPx + CellPadding, MinColumnWidth, MaxColumnWidth);

            columns[c] = new CsvColumn(names[c], width);
            headerCells[c] = new CsvCell(names[c], width);
            total += width;
        }

        return new CsvStructure(columns, headerCells, total);
    }

    /// <summary>
    /// The same grid with different column labels - what the CSV viewer's "first row is header"
    /// tickbox does. Widths are deliberately carried over untouched rather than recomputed: they
    /// were measured from the data, and relabelling a column does not change what is in it.
    /// Cheap enough to call per toggle (one array of each per column, no file read).
    /// </summary>
    public CsvStructure WithNames(IReadOnlyList<string> names)
    {
        var renamed = new CsvColumn[this.columns.Length];
        var headerCells = new CsvCell[this.columns.Length];

        for (int c = 0; c < renamed.Length; c++)
        {
            string name = c < names.Count ? names[c] : string.Empty;
            renamed[c] = new CsvColumn(name, this.columns[c].Width);
            headerCells[c] = new CsvCell(name, this.columns[c].Width);
        }

        return new CsvStructure(renamed, headerCells, TotalWidth);
    }
}
