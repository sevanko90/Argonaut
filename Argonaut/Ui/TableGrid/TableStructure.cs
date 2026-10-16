using System;
using System.Collections.Generic;

namespace Argonaut.Ui.TableGrid;

/// <summary>A cell's display text. Width is the column's business, not the cell's - the grid
/// sizes columns, so a realized row carries no geometry and never goes stale when one is
/// resized or the content font changes.</summary>
public readonly record struct TableCell(string Text);

/// <summary>
/// One column: its display name and the character count its width is derived from. Pixels are
/// deliberately not stored - the grid turns the count into a width in the font it is actually
/// drawing with (the status bar can swap the content font at runtime), not the one that was
/// current at discovery.
/// </summary>
public readonly record struct TableColumn(string Name, int MaxChars);

/// <summary>
/// The shape of a CSV-style grid as one immutable object: the column names and the character
/// counts their widths open at. Constructed by whoever knows the data, consumed by
/// whoever renders it - this type discovers nothing.
///
/// Column *identity* cannot live here because it has three different sources: a CSV header
/// line, the property names a JSON array's elements share, or "Column 1..N" placeholders a
/// dropdown chose. All three end up as names plus a per-column maximum character count, which
/// is the whole input <see cref="FromMaxChars"/> takes - so each caller measures in whatever
/// currency is cheapest for its own data (a CsvFieldSpan's byte length; a JSON value's) and
/// nothing in here ever sees text.
///
/// Widths are a character COUNT rather than a text measurement, per CLAUDE.md's low-allocation
/// guidance - laying out every value of a multi-gigabyte file to size a column is exactly what
/// this app cannot do. The count becomes pixels in the grid, from the measured advance of the font
/// it draws with; content that still doesn't fit is handled by the view with
/// ellipsis + a tooltip, not by resizing. The counts come from a sample of the rows available at
/// first paint and are fixed for this structure's lifetime - a *new* structure replaces it when
/// the grid's shape changes.
///
/// A sealed class rather than a struct: it holds arrays either way, it is allocated once per
/// shape change rather than per row, and an immutable class avoids the defensive-copy traps a
/// struct with array fields invites.
/// </summary>
public sealed class TableStructure
{
    /// <summary>Narrowest a discovered column opens, in characters - enough that a column of
    /// short values still reads as a column. A width the user chose - by dragging, or by
    /// double-clicking a resizer to fit the column to its content - is theirs, and is not
    /// second-guessed against this.</summary>
    internal const int MinColumnChars = 6;

    /// <summary>Widest a column opens from discovery, in characters. Past this the value is
    /// long enough that the tooltip, not the column, is how it gets read.</summary>
    internal const int MaxDiscoveredChars = 40;

    private readonly TableColumn[] columns;

    private TableStructure(TableColumn[] columns) => this.columns = columns;

    public IReadOnlyList<TableColumn> Columns => this.columns;

    public int ColumnCount => this.columns.Length;

    /// <summary>
    /// The one factory: the columns, each with its opening width as a character count.
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
    public static TableStructure FromMaxChars(IReadOnlyList<string> names, ReadOnlySpan<int> maxChars)
    {
        var columns = new TableColumn[names.Count];

        for (int c = 0; c < columns.Length; c++)
        {
            int chars = c < maxChars.Length ? maxChars[c] : names[c].Length;
            columns[c] = new TableColumn(names[c], Math.Clamp(chars, MinColumnChars, MaxDiscoveredChars));
        }

        return new TableStructure(columns);
    }

    /// <summary>
    /// The same grid with different column labels - what the CSV viewer's "first row is header"
    /// tickbox does. Widths are deliberately carried over untouched rather than recomputed: they
    /// were measured from the data, and relabelling a column does not change what is in it.
    /// Cheap enough to call per toggle (one array of each per column, no file read).
    /// </summary>
    public TableStructure WithNames(IReadOnlyList<string> names)
    {
        var renamed = new TableColumn[this.columns.Length];

        for (int c = 0; c < renamed.Length; c++)
        {
            string name = c < names.Count ? names[c] : string.Empty;
            renamed[c] = new TableColumn(name, this.columns[c].MaxChars);
        }

        return new TableStructure(renamed);
    }
}
