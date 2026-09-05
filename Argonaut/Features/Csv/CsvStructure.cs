using System;
using System.Collections.Generic;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Csv;

/// <summary>A cell's display text. Width is the column's business, not the cell's - the grid
/// sizes columns, so a realized row carries no geometry and never goes stale when one is
/// resized or the content font changes.</summary>
public readonly record struct CsvCell(string Text);

/// <summary>
/// One column: its display name and the character count its width is derived from. Pixels are
/// deliberately not stored - <see cref="Width"/> asks <see cref="CellTextMetrics"/> every time,
/// so the answer follows the font the app is actually rendering with (the status bar can swap
/// the content font at runtime) rather than the one that was current at discovery.
/// </summary>
public readonly record struct CsvColumn(string Name, int MaxChars)
{
    public double Width => CellTextMetrics.Current.WidthForChars(MaxChars);
}

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
/// Widths are a character COUNT rather than a text measurement, per CLAUDE.md's low-allocation
/// guidance - laying out every value of a multi-gigabyte file to size a column is exactly what
/// this app cannot do. The count becomes pixels through <see cref="CellTextMetrics"/>, whose two
/// terms are themselves measured; content that still doesn't fit is handled by the view with
/// ellipsis + a tooltip, not by resizing. The counts come from a sample of the rows available at
/// first paint and are fixed for this structure's lifetime - a *new* structure replaces it when
/// the grid's shape changes.
///
/// A sealed class rather than a struct: it holds arrays either way, it is allocated once per
/// shape change rather than per row, and an immutable class avoids the defensive-copy traps a
/// struct with array fields invites.
/// </summary>
public sealed class CsvStructure
{
    /// <summary>Narrowest a discovered column opens, in characters - enough that a column of
    /// short values still reads as a column. A width the user chose - by dragging, or by
    /// double-clicking a resizer to fit the column to its content - is theirs, and is not
    /// second-guessed against this.</summary>
    private const int MinColumnChars = 6;

    /// <summary>Widest a column opens from discovery, in characters. Past this the value is
    /// long enough that the tooltip, not the column, is how it gets read.</summary>
    private const int MaxDiscoveredChars = 40;

    private readonly CsvColumn[] columns;

    private CsvStructure(CsvColumn[] columns) => this.columns = columns;

    public IReadOnlyList<CsvColumn> Columns => this.columns;

    public int ColumnCount => this.columns.Length;

    public double TotalWidth
    {
        get
        {
            double total = 0;
            foreach (var column in this.columns)
                total += column.Width;

            return total;
        }
    }

    /// <summary>The narrowest a discovered column opens, in pixels.</summary>
    public static double MinColumnWidth => WidthForChars(MinColumnChars);

    /// <summary>
    /// Pixel width for a character count, with no discovery ceiling applied - what discovery and
    /// a fit-to-content double-click both go through, so the two can never disagree. Both terms
    /// of the conversion are measured; see <see cref="CellTextMetrics"/>.
    /// </summary>
    public static double WidthForChars(int chars) => CellTextMetrics.Current.WidthForChars(chars);

    /// <summary>
    /// The one factory: per-column pixel widths from a per-column maximum character count.
    /// <paramref name="maxChars"/> is indexed in parallel with <paramref name="names"/>; a
    /// column the caller had no counts for (a shorter span) is widthed from its name alone.
    /// Callers are expected to seed each count with the name's own length where the header
    /// must always fit.
    ///
    /// The count saturates - it is clamped into
    /// <see cref="MinColumnChars"/>..<see cref="MaxDiscoveredChars"/> - which is why an
    /// over-counting measure (UTF-8 bytes rather than characters, a CSV field's length including
    /// its quotes) is harmless here.
    /// </summary>
    public static CsvStructure FromMaxChars(IReadOnlyList<string> names, ReadOnlySpan<int> maxChars)
    {
        var columns = new CsvColumn[names.Count];

        for (int c = 0; c < columns.Length; c++)
        {
            int chars = c < maxChars.Length ? maxChars[c] : names[c].Length;
            columns[c] = new CsvColumn(names[c], Math.Clamp(chars, MinColumnChars, MaxDiscoveredChars));
        }

        return new CsvStructure(columns);
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

        for (int c = 0; c < renamed.Length; c++)
        {
            string name = c < names.Count ? names[c] : string.Empty;
            renamed[c] = new CsvColumn(name, this.columns[c].MaxChars);
        }

        return new CsvStructure(renamed);
    }
}
