using System;
using System.Collections.Generic;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;

namespace Argonaut.Features.Csv;

public sealed partial class TableGridColumns
{
    // Keep column identity, templates and user widths while their cells are off-screen.
    // TableView only receives the viewport plus two columns of overscan on either side.
    private const int OverscanColumns = 2;
    private readonly List<TableViewColumn> logicalColumns = [];
    private readonly TableViewColumn leadingSpace = SpacerColumn();
    private readonly TableViewColumn trailingSpace = SpacerColumn();
    private int firstRealizedColumn = -1;
    private int lastRealizedColumn = -1;
    private double windowOffset = double.NaN;
    private double windowWidth = double.NaN;
    private double windowScale = double.NaN;
    private bool columnWidthsChanged;
    private bool windowRefreshQueued;
    private bool disposed;
    private bool resizingColumn;
    private bool refreshingWindow;

    /// <summary>The source column for a realized column, or -1 for a spacer.</summary>
    public int LogicalColumnIndex(TableViewColumn column)
        => column is SourceColumn source && source.SourceIndex < this.logicalColumns.Count ? source.SourceIndex : -1;

    /// <summary>Hit testing in row coordinates, including the width of hidden columns.</summary>
    public int LogicalColumnAt(double x)
    {
        if (x < 0)
            return -1;

        double edge = 0;
        foreach (var column in this.table.Columns)
        {
            edge += column.ActualWidth;
            if (x < edge)
                return LogicalColumnIndex(column);
        }

        return -1;
    }

    private static TableViewColumn SpacerColumn() => new()
    {
        CanUserResize = false,
        HeaderTemplate = new FuncDataTemplate<object>((_, _) => null),
        CellTemplate = new FuncDataTemplate<object>((_, _) => null),
        HeaderTheme = SpacerTheme(typeof(TableViewColumnHeader)),
        CellTheme = SpacerTheme(typeof(TableViewCell)),
        Width = new GridLength(0),
    };

    // Spacers carry only geometry: no theme chrome, resizer or hit target.
    private static ControlTheme SpacerTheme(Type targetType) => new()
    {
        TargetType = targetType,
        Setters =
        {
            new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
            new Setter(Layoutable.MinWidthProperty, 0.0),
            new Setter(InputElement.IsHitTestVisibleProperty, false),
        },
    };

    private void OnColumnWidthChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TableViewColumn.WidthProperty)
        {
            this.columnWidthsChanged = true;
            QueueColumnWindow();
        }
    }

    private void OnRealizedWidthChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (this.refreshingWindow || e.Property != TableViewColumn.WidthProperty || sender is not SourceColumn column)
            return;

        int index = LogicalColumnIndex(column);
        if (index >= 0)
            this.logicalColumns[index].Width = column.Width;
    }

    private void ReleaseRealizedColumns()
    {
        foreach (var column in this.table.Columns)
            column.PropertyChanged -= OnRealizedWidthChanged;
    }

    private void OnViewportLayout(object? sender, EventArgs e)
    {
        var scroll = this.table.Scroll;
        if (scroll is not null && (scroll.Offset.X != this.windowOffset
            || scroll.Viewport.Width != this.windowWidth || CurrentLayoutScale != this.windowScale
            || this.columnWidthsChanged))
            QueueColumnWindow();
    }

    private double CurrentLayoutScale
        => this.table.UseLayoutRounding ? TopLevel.GetTopLevel(this.table)?.RenderScaling ?? 1 : 0;

    // Sum what TableView actually lays out. Rounding one large spacer after summing raw
    // fractional widths differs from rounding each hidden column, shifting the scroll extent.
    private double ColumnLayoutWidth(TableViewColumn column)
        => this.windowScale > 0 ? LayoutHelper.RoundLayoutValue(column.Width.Value, this.windowScale) : column.Width.Value;

    private void QueueColumnWindow()
    {
        if (this.windowRefreshQueued || this.disposed || this.resizingColumn)
            return;

        this.windowRefreshQueued = true;
        // Width changes can originate inside a resizer gesture; layout callbacks are also
        // inside TableView's work. Replace its column collection only after that work unwinds.
        UiDeferral.AfterCurrentInput(() =>
        {
            this.windowRefreshQueued = false;
            if (!this.disposed && !this.resizingColumn)
                RefreshColumnWindow();
        });
    }

    private void OnResizerReleased(object? sender, PointerReleasedEventArgs e) => FinishColumnResize();

    private void OnResizerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => FinishColumnResize();

    private void FinishColumnResize()
    {
        if (!this.resizingColumn)
            return;

        // Keep the captured header alive throughout a drag even when its width changes which
        // columns fit. Re-windowing during the drag would destroy the thumb under the pointer.
        this.resizingColumn = false;
        QueueColumnWindow();
    }

    private void RefreshColumnWindow()
    {
        var scroll = this.table.Scroll;
        double viewportWidth = scroll?.Viewport.Width ?? this.table.Bounds.Width;
        double offset = scroll?.Offset.X ?? 0;
        bool preserveCellCount = this.firstRealizedColumn >= 0 && viewportWidth == this.windowWidth
            && !this.columnWidthsChanged;
        this.windowOffset = offset;
        this.windowWidth = viewportWidth;
        this.windowScale = CurrentLayoutScale;
        this.columnWidthsChanged = false;

        int count = this.logicalColumns.Count;
        double totalWidth = 0;
        foreach (var column in this.logicalColumns)
            totalWidth += ColumnLayoutWidth(column);

        // Re-shaping or shrinking the grid can leave the previous offset past the new end.
        offset = Math.Clamp(offset, 0, Math.Max(0, totalWidth - viewportWidth));
        int first = 0;
        double edge = 0;
        while (first < count && edge + ColumnLayoutWidth(this.logicalColumns[first]) <= offset)
            edge += ColumnLayoutWidth(this.logicalColumns[first++]);

        int last = first;
        while (last < count && edge < offset + viewportWidth)
            edge += ColumnLayoutWidth(this.logicalColumns[last++]);

        first = Math.Max(0, first - OverscanColumns);
        last = Math.Min(count, last + OverscanColumns);
        // TableView recycles its cell controls only when the column count stays unchanged.
        // A partially visible column must not make that count oscillate on every boundary.
        // Keep at most two extra existing slots during horizontal movement;
        // a resize/font/shape change is free to reduce the count again.
        if (preserveCellCount)
        {
            int retainedCount = Math.Min(this.lastRealizedColumn - this.firstRealizedColumn,
                last - first + OverscanColumns);
            last = Math.Min(count, Math.Max(last, first + retainedCount));
            first = Math.Max(0, Math.Min(first, last - retainedCount));
        }
        double leadingWidth = 0;
        double trailingWidth = 0;
        for (int c = 0; c < first; c++)
            leadingWidth += ColumnLayoutWidth(this.logicalColumns[c]);
        for (int c = last; c < count; c++)
            trailingWidth += ColumnLayoutWidth(this.logicalColumns[c]);

        this.leadingSpace.Width = new GridLength(leadingWidth);
        this.trailingSpace.Width = new GridLength(trailingWidth);

        if (first != this.firstRealizedColumn || last != this.lastRealizedColumn)
            RemoveMark();
        this.firstRealizedColumn = first;
        this.lastRealizedColumn = last;
        bool hasHiddenColumns = first > 0 || last < count;
        int spacerCount = hasHiddenColumns ? 2 : 0;
        bool replaceSlots = this.table.Columns.Count != last - first + spacerCount
            || (hasHiddenColumns && !ReferenceEquals(this.table.Columns[0], this.leadingSpace))
            || (!hasHiddenColumns && this.table.Columns.Count > 0 && ReferenceEquals(this.table.Columns[0], this.leadingSpace));
        var realized = replaceSlots ? new AvaloniaList<TableViewColumn>() : this.table.Columns;
        this.refreshingWindow = true;
        try
        {
            if (replaceSlots && hasHiddenColumns)
                realized.Add(this.leadingSpace);
            for (int c = first; c < last; c++)
            {
                var source = this.logicalColumns[c];
                var slot = replaceSlots ? new SourceColumn(c)
                    : (SourceColumn)realized[c - first + (hasHiddenColumns ? 1 : 0)];
                slot.ShowSourceColumn(c);
                slot.Width = source.Width;
                slot.Header = source.Header;
                slot.HeaderTemplate = source.HeaderTemplate;
                slot.CellTemplate = source.CellTemplate;
                if (replaceSlots)
                {
                    slot.PropertyChanged += OnRealizedWidthChanged;
                    realized.Add(slot);
                }
            }
            if (replaceSlots)
            {
                if (hasHiddenColumns)
                    realized.Add(this.trailingSpace);
                ReleaseRealizedColumns();
                // TableView rebuilds on every collection notification, including insert/remove.
                // Change its collection only when the viewport needs a different slot count.
                this.table.Columns = realized;
            }
        }
        finally
        {
            this.refreshingWindow = false;
        }
    }
}
