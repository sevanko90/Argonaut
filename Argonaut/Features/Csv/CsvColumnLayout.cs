using System;
using System.Collections.Generic;
using System.Linq;

namespace Argonaut.Features.Csv;

/// <summary>A cell's display text plus its column's fixed width, so header and data cells can
/// bind directly without a per-item lookup into a shared array.</summary>
public readonly record struct CsvCell(string Text, double Width);

/// <summary>
/// Per-column pixel widths for a CSV/TSV grid, computed once from the header plus whatever
/// rows were available in the initial indexed batch (see CsvViewModel.LoadAsync), then fixed -
/// never recomputed as more rows stream in from the background index. This is a character-count
/// heuristic (no text measurement), per CLAUDE.md's low-allocation/low-overhead guidance:
/// content that doesn't fit is handled by the view with ellipsis + a tooltip, not by resizing.
/// </summary>
public sealed class CsvColumnLayout
{
    private const double CharWidthPx = 7.0;
    private const double CellPadding = 16.0;
    private const double MinColumnWidth = 60.0;
    private const double MaxColumnWidth = 320.0;

    private readonly double[] widths;

    public IReadOnlyList<double> Widths => widths;

    public int ColumnCount => widths.Length;

    public double TotalWidth { get; }

    private CsvColumnLayout(double[] widths)
    {
        this.widths = widths;
        TotalWidth = widths.Sum();
    }

    public static CsvColumnLayout Compute(IReadOnlyList<string> headerFields, IReadOnlyList<string[]> sampleRows)
    {
        var widths = new double[headerFields.Count];
        for (int c = 0; c < widths.Length; c++)
        {
            int maxChars = headerFields[c].Length;
            foreach (var row in sampleRows)
            {
                if (c < row.Length && row[c].Length > maxChars)
                    maxChars = row[c].Length;
            }

            widths[c] = Math.Clamp(maxChars * CharWidthPx + CellPadding, MinColumnWidth, MaxColumnWidth);
        }

        return new CsvColumnLayout(widths);
    }

    /// <summary>Width for a column index, including ones beyond <see cref="ColumnCount"/> (a
    /// row with more fields than the header) - those fall back to the minimum column width.</summary>
    public double WidthFor(int columnIndex)
        => columnIndex >= 0 && columnIndex < widths.Length ? widths[columnIndex] : MinColumnWidth;
}
