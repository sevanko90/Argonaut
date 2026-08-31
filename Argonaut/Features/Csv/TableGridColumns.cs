using System;
using System.Linq;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Argonaut.Features.Csv;

/// <summary>
/// Projects a <see cref="CsvStructure"/> onto a <see cref="TableView"/>'s columns, and adds the
/// one resizing gesture TableView does not have: double-clicking a column's resizer fits that
/// column to its content.
///
/// Columns are built here rather than declared in XAML because their number and names are data:
/// a CSV header line, a JSON array's shared property names, or "Column 1..N" placeholders, all
/// re-discovered whenever the grid is re-shaped. Each column binds its cells by index
/// (<c>Cells[i].Text</c>) into <see cref="CsvVisibleRow"/>, so the row objects the virtualizing
/// panel realizes stay exactly what they were under the hand-rolled grid.
///
/// Drag widths are deliberately un-policed - no floor, no ceiling. A clamp can only be applied
/// after the fact (TableView exposes no MinWidth/MaxWidth and its ActualWidth is read-only), so
/// it shows up as the column springing back out from under the pointer, which reads worse than
/// the too-wide column it was preventing.
///
/// It also teaches <see cref="CellTextMetrics"/> what a cell's chrome costs. Nothing but a
/// realized cell knows that (the cell theme's padding is a dynamic resource, and an unattached
/// cell never applies its theme), so the first layout after a rebuild measures one and, if that
/// changed the answer, re-applies the widths that were seeded without it.
/// </summary>
public sealed class TableGridColumns : IDisposable
{
    private readonly TableView table;
    private IColumnFitSource? fitSource;
    private CsvStructure? seeded;

    public TableGridColumns(TableView table)
    {
        this.table = table;
        this.table.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Replaces every column with one per column of <paramref name="structure"/>, seeding each
    /// width from the structure's character-count heuristic. Any width the user had dragged is
    /// dropped with the column it belonged to - a re-shape means these are different columns,
    /// not the same ones renamed.
    ///
    /// <paramref name="fitSource"/> is the body whose realized rows answer a fit-to-content
    /// double-click; null disables the gesture (the columns still resize by dragging).
    /// </summary>
    public void Rebuild(CsvStructure structure, IColumnFitSource? fitSource = null)
    {
        this.fitSource = fitSource;
        this.seeded = structure;
        this.table.Columns.Clear();

        for (int c = 0; c < structure.ColumnCount; c++)
        {
            var source = structure.Columns[c];
            this.table.Columns.Add(new TableViewColumn
            {
                Header = source.Name,
                Width = new GridLength(source.Width),
                CellTemplate = CellTemplate(c),
            });
        }

        this.table.LayoutUpdated += OnFirstLayoutAfterRebuild;
    }

    public void Dispose()
    {
        this.table.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        this.table.LayoutUpdated -= OnFirstLayoutAfterRebuild;
        this.table.Columns.Clear();
        this.fitSource = null;
        this.seeded = null;
    }

    /// <summary>
    /// One-shot: measure what a realized cell adds around its text and, if that is news, re-seed
    /// the widths that were computed without it. Runs before the user can have touched a resizer
    /// (it is the layout pass that first drew these columns), so it cannot overwrite a chosen
    /// width; once the session has learned the inset, later rebuilds seed correctly and this
    /// finds nothing to do.
    /// </summary>
    private void OnFirstLayoutAfterRebuild(object? sender, EventArgs e)
    {
        if (MeasuredCellInset() is not { } inset)
            return;

        this.table.LayoutUpdated -= OnFirstLayoutAfterRebuild;

        if (!CellTextMetrics.ReportCellInset(inset) || this.seeded is not { } structure)
            return;

        for (int c = 0; c < this.table.Columns.Count && c < structure.ColumnCount; c++)
            this.table.Columns[c].Width = new GridLength(structure.Columns[c].Width + inset);
    }

    /// <summary>
    /// The horizontal chrome around a realized cell's text, or null while no cell has been
    /// realized yet. Read from the cell's own resolved padding and border rather than by
    /// differencing bounds: a cell whose text is shorter than the column arranges that text to
    /// its own width, so the leftover would be counted as chrome and the columns would grow by
    /// it on every rebuild.
    /// </summary>
    private double? MeasuredCellInset()
    {
        foreach (var visual in this.table.GetVisualDescendants())
        {
            if (visual is not TableViewCell { Bounds.Width: > 0 } cell)
                continue;

            return cell.Padding.Left + cell.Padding.Right + cell.BorderThickness.Left + cell.BorderThickness.Right;
        }

        return null;
    }

    /// <summary>
    /// One template per column, with the column's index baked into its bindings: a cell
    /// template's DataContext is the whole <see cref="CsvVisibleRow"/>, so the index is the only
    /// thing that tells one column's cells from another's. Trimming plus a tooltip carrying the
    /// untrimmed text is what makes a too-narrow column readable without resizing it.
    /// </summary>
    private static IDataTemplate CellTemplate(int columnIndex)
        => new FuncDataTemplate<CsvVisibleRow>((_, _) =>
        {
            var cellText = new Binding($"Cells[{columnIndex}].Text");
            var text = new TextBlock
            {
                // Vertical only: the horizontal inset is the TableViewCell's own, and the metrics
                // learn it by measuring. Adding a horizontal margin here would spend width no
                // measurement accounts for - which is exactly what trimmed values that fit.
                Margin = new Thickness(0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };

            text.Bind(TextBlock.TextProperty, cellText);
            text.Bind(ToolTip.TipProperty, cellText);
            // Font from the same two resources CellTextMetrics measures, so the width budget and
            // the text it is budgeting for can never be for different fonts.
            text.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("AppContentFontFamily"));
            text.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("AppContentFontSize"));
            return text;
        }, supportsRecycling: true);

    /// <summary>
    /// Double-click on a column's resizer: fit the column to what its realized rows hold. Handled
    /// as a tunnelled press rather than DoubleTapped because the resizer Thumb takes the pointer
    /// on the way down and never routes a tap back out.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || this.fitSource is null)
            return;

        if (ResizedColumn(e.Source as Visual) is not { } column)
            return;

        int columnIndex = this.table.Columns.IndexOf(column);
        if (columnIndex < 0)
            return;

        int chars = Math.Max(column.Header?.ToString()?.Length ?? 0, this.fitSource.LongestRealizedText(columnIndex));
        if (chars <= 0)
            return;

        e.Handled = true;

        // Deferred for the reason UiDeferral exists: writing Width from inside the pointer event
        // the resizer is still handling re-enters TableView's measure mid-gesture, and that path
        // throws "Cannot call Measure using a size with NaN values" out of the layout pass.
        UiDeferral.AfterCurrentInput(() => column.Width = new GridLength(CsvStructure.WidthForChars(chars)));
    }

    /// <summary>The column whose resizer <paramref name="source"/> sits in, or null if the press
    /// was anywhere else - a header's label, a cell, the scrollbar.</summary>
    private static TableViewColumn? ResizedColumn(Visual? source)
    {
        if (source is null)
            return null;

        bool onResizer = source is Thumb { Name: "PART_Resizer" }
            || source.FindAncestorOfType<Thumb>() is { Name: "PART_Resizer" };

        return onResizer ? source.FindAncestorOfType<TableViewColumnHeader>()?.Column : null;
    }
}
