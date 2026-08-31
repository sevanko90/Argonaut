using System;
using System.Collections.Generic;
using Argonaut.Features.Search;
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
using Avalonia.Reactive;
using Avalonia.VisualTree;

namespace Argonaut.Features.Csv;

/// <summary>
/// Projects a <see cref="CsvStructure"/> onto a <see cref="TableView"/>'s columns - shared by
/// both grids, the CSV viewer and the JSON array table - and adds the two things TableView has
/// no answer for: fitting a column to its content, and keeping widths honest when what they
/// were measured against changes.
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
/// Two things do move a width after it is seeded, and both leave a column the user has sized
/// alone (a drag writes <see cref="TableViewColumn.Width"/>, so a column whose width is no
/// longer the one last applied here is theirs):
///
///   * the cell inset, once a realized cell can be measured for it - nothing else knows it, so
///     the first layout after a rebuild teaches <see cref="CellTextMetrics"/> and re-seeds;
///   * the content font, which the status bar can swap at runtime. The widths were measured for
///     the outgoing face, so the metrics re-measure and the columns follow.
/// </summary>
public sealed class TableGridColumns : IDisposable
{
    private readonly TableView table;
    private readonly List<double> appliedWidths = [];
    private readonly List<IDisposable> fontSubscriptions = [];

    private IColumnFitSource? fitSource;
    private CsvStructure? seeded;
    private IReadOnlyList<object>? seededHeaders;

    public TableGridColumns(TableView table)
    {
        this.table = table;
        this.table.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);

        foreach (string key in new[] { "AppContentFontFamily", "AppContentFontSize" })
        {
            // A resource observable replays the current value on subscribe; that first one is
            // what the columns were already built for, so only later changes are a font swap.
            bool subscribing = true;
            this.fontSubscriptions.Add(this.table.GetResourceObservable(key)
                .Subscribe(new AnonymousObserver<object?>(_ =>
                {
                    if (subscribing)
                        subscribing = false;
                    else
                        OnContentFontChanged();
                })));
        }
    }

    /// <summary>
    /// Replaces every column with one per column of <paramref name="structure"/>, widthed from
    /// the character count it discovered. Any width the user had dragged is dropped with the
    /// column it belonged to - a re-shape means these are different columns, not the same ones
    /// renamed.
    ///
    /// <paramref name="fitSource"/> is the body whose realized rows answer a fit-to-content
    /// double-click; null disables the gesture (columns still resize by dragging).
    /// <paramref name="highlightTerm"/> binds the find term into every cell and header, for the
    /// grid that has a search navigator; null renders plain text.
    ///
    /// <paramref name="headers"/> replaces the plain string labels with one content object per
    /// column, rendered by <paramref name="headerTemplate"/> - what the JSON array table's
    /// clickable route headers arrive as. A grid whose headers are plain text passes neither.
    /// </summary>
    public void Rebuild(CsvStructure structure, IColumnFitSource? fitSource = null, BindingBase? highlightTerm = null,
        IReadOnlyList<object>? headers = null, IDataTemplate? headerTemplate = null)
    {
        this.fitSource = fitSource;

        // A relabelling is not a re-shape: CSV's "first row is header" tickbox publishes a new
        // structure with the same columns under different names, and rebuilding for that would
        // throw away every width the user had set. Same count, same discovered widths - so the
        // columns are the same columns, and only their labels change.
        //
        // Widths alone cannot say that, though: expanding a column in the JSON array table can
        // land on the same count and the same measured widths while every column is now a
        // DIFFERENT one, and the cell templates bind by index. So a grid that supplies its own
        // headers says which columns these are by supplying a new list - identity the count
        // cannot carry.
        if (this.seeded is { } previous && SameShape(previous, structure)
            && ReferenceEquals(this.seededHeaders, headers))
        {
            this.seeded = structure;
            for (int c = 0; c < this.table.Columns.Count && c < structure.ColumnCount; c++)
            {
                // Only the plain-label grids relabel from the structure. A grid that supplies
                // header content keeps it: writing the structure's name over it here would strip
                // a route header back to a string the second time the same shape is published,
                // which the view does whenever it rebuilds for an unchanged view model.
                this.table.Columns[c].Header = headers is not null && c < headers.Count
                    ? headers[c]
                    : structure.Columns[c].Name;
            }

            return;
        }

        this.seeded = structure;
        this.seededHeaders = headers;
        this.table.Columns.Clear();
        this.appliedWidths.Clear();

        for (int c = 0; c < structure.ColumnCount; c++)
        {
            var source = structure.Columns[c];
            this.table.Columns.Add(new TableViewColumn
            {
                Header = headers is not null && c < headers.Count ? headers[c] : source.Name,
                Width = new GridLength(source.Width),
                HeaderTemplate = headerTemplate ?? (highlightTerm is null ? null : HeaderTemplate(highlightTerm)),
                CellTemplate = CellTemplate(c, highlightTerm),
            });

            this.appliedWidths.Add(source.Width);
        }

        this.table.LayoutUpdated += OnFirstLayoutAfterRebuild;
    }

    public void Dispose()
    {
        this.table.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        this.table.LayoutUpdated -= OnFirstLayoutAfterRebuild;

        foreach (var subscription in this.fontSubscriptions)
            subscription.Dispose();
        this.fontSubscriptions.Clear();

        this.table.Columns.Clear();
        this.appliedWidths.Clear();
        this.fitSource = null;
        this.seeded = null;
        this.seededHeaders = null;
    }

    private static bool SameShape(CsvStructure previous, CsvStructure next)
    {
        if (previous.ColumnCount != next.ColumnCount)
            return false;

        for (int c = 0; c < previous.ColumnCount; c++)
        {
            if (previous.Columns[c].MaxChars != next.Columns[c].MaxChars)
                return false;
        }

        return true;
    }

    /// <summary>
    /// One template per column, with the column's index baked into its bindings: a cell
    /// template's DataContext is the whole <see cref="CsvVisibleRow"/>, so the index is the only
    /// thing that tells one column's cells from another's. Trimming plus a tooltip carrying the
    /// untrimmed text is what makes a too-narrow column readable without resizing it.
    /// </summary>
    private static IDataTemplate CellTemplate(int columnIndex, BindingBase? highlightTerm)
        => new FuncDataTemplate<CsvVisibleRow>((_, _) =>
        {
            var text = CellTextBlock();
            var cellText = new Binding($"Cells[{columnIndex}].Text");

            text.Bind(ToolTip.TipProperty, cellText);
            BindText(text, cellText, highlightTerm);
            return text;
        }, supportsRecycling: true);

    /// <summary>The column label, in the same highlight-aware shape as a cell - a find match on a
    /// CSV header line has to light up where the user can see it.</summary>
    private static IDataTemplate HeaderTemplate(BindingBase highlightTerm)
        => new FuncDataTemplate<object>((_, _) =>
        {
            var text = CellTextBlock();
            text.FontWeight = FontWeight.SemiBold;
            BindText(text, new Binding("."), highlightTerm);
            return text;
        }, supportsRecycling: true);

    private static TextBlock CellTextBlock()
    {
        var text = new TextBlock
        {
            // Vertical only: the horizontal inset is the TableViewCell's own, and the metrics
            // learn it by measuring. Adding a horizontal margin here would spend width no
            // measurement accounts for - which is exactly what trimmed values that fit.
            Margin = new Thickness(0, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        // The same two resources CellTextMetrics measures, so the width budget and the text it
        // is budgeting for can never be for different fonts.
        text.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("AppContentFontFamily"));
        text.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("AppContentFontSize"));
        return text;
    }

    /// <summary>Plain text, or text with the find term highlighted inside it when the grid has a
    /// term to bind - the highlight path replaces TextBlock.Text rather than layering on it.</summary>
    private static void BindText(TextBlock text, BindingBase content, BindingBase? highlightTerm)
    {
        if (highlightTerm is null)
        {
            text.Bind(TextBlock.TextProperty, content);
            return;
        }

        text.Bind(SearchHighlight.TextProperty, content);
        text.Bind(SearchHighlight.TermProperty, highlightTerm);
    }

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

    /// <summary>
    /// One-shot: measure what a realized cell adds around its text and, if that is news, re-seed.
    /// Runs in the layout pass that first drew these columns, so the user cannot have resized one
    /// yet; once a session has learned the inset, later rebuilds seed correctly and this finds
    /// nothing to do.
    /// </summary>
    private void OnFirstLayoutAfterRebuild(object? sender, EventArgs e)
    {
        if (MeasuredCellInset() is not { } inset)
            return;

        this.table.LayoutUpdated -= OnFirstLayoutAfterRebuild;

        if (CellTextMetrics.ReportCellInset(inset))
            ReseedUntouchedColumns();
    }

    /// <summary>
    /// The status bar can repoint AppContentFontFamily at the mono or sans family while a grid is
    /// showing. Every width in it was measured for the outgoing face, so the metrics re-measure
    /// and the columns follow - otherwise the cells re-font and the columns do not, which is how
    /// a column ends up too narrow for text that used to fit.
    /// </summary>
    private void OnContentFontChanged()
    {
        CellTextMetrics.InvalidateFont();
        ReseedUntouchedColumns();
    }

    /// <summary>
    /// Re-applies the discovered width to every column still sitting at the width this last
    /// applied. A drag or a fit-to-content writes <see cref="TableViewColumn.Width"/>, so a
    /// column that differs is one the user chose, and nothing here overrules that.
    /// </summary>
    private void ReseedUntouchedColumns()
    {
        if (this.seeded is not { } structure)
            return;

        for (int c = 0; c < this.table.Columns.Count && c < structure.ColumnCount && c < this.appliedWidths.Count; c++)
        {
            var column = this.table.Columns[c];
            if (Math.Abs(column.Width.Value - this.appliedWidths[c]) > 0.5)
                continue;

            double width = structure.Columns[c].Width;
            column.Width = new GridLength(width);
            this.appliedWidths[c] = width;
        }
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
}
