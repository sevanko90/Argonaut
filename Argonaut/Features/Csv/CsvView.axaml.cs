using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Argonaut.Ui.Rows;
using Argonaut.Ui.TableGrid;

namespace Argonaut.Features.Csv;

/// <summary>
/// The CSV grid's code-behind. Three jobs: hand the surface the view model's rows and
/// <see cref="CsvViewModel.Structure"/> once they exist (the "first row is header" tickbox
/// relabels the columns), reveal what a search match selected, and dispose the document on
/// detach as the idempotent safety net the shell doesn't drive (window close).
///
/// The grid itself - header, gutter, cells, selection, resizing - is the surface's; its
/// scrollbars are driven by <see cref="RowScrollBars"/>.
/// </summary>
public partial class CsvView : UserControl
{
    private readonly RowScrollBars scrollBars;
    private CsvViewModel? subscribedViewModel;

    public CsvView()
    {
        InitializeComponent();

        this.scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Surface.PropertyChanged += OnSurfacePropertyChanged;
        Surface.ScrollPositionChanged += OnSurfaceScrolled;
    }

    /// <summary>The row the user selected is where the document is - tells the view model, so a
    /// filter reload or a view switch can land back on it.</summary>
    private void OnSurfacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TableSurface.SelectedIndexProperty)
            this.subscribedViewModel?.RecordSelectedRow(Surface.SelectedIndex >= 0 ? Surface.SelectedIndex : null);
    }

    /// <summary>The grid moved, or its rows or size changed: tells the view model which rows are on
    /// screen, so a reload or a view switch can come back to them when nothing selected is in sight.</summary>
    private void OnSurfaceScrolled(object? sender, EventArgs e)
    {
        var (first, last) = Surface.RowsOnScreen();
        this.subscribedViewModel?.RecordRowsOnScreen(first, last);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.subscribedViewModel is not null)
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        this.subscribedViewModel = DataContext as CsvViewModel;
        Surface.SelectedIndex = -1;
        this.scrollBars.ResetPan();
        if (this.subscribedViewModel is null)
        {
            Surface.Rows = null;
            Surface.Structure = null;
            return;
        }

        this.subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowDocument(this.subscribedViewModel);
    }

    /// <summary>
    /// Reveals a search match (CsvSearchNavigator.SelectRow): the row selected and centred, the
    /// column panned into view. A new structure means new column labels (the tickbox).
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CsvViewModel vm)
            return;

        if (e.PropertyName is null or nameof(CsvViewModel.Structure) or nameof(CsvViewModel.Rows))
            ShowDocument(vm);

        if (e.PropertyName is null or nameof(CsvViewModel.SelectedRowIndex))
        {
            Surface.SelectedIndex = vm.SelectedRowIndex ?? -1;
            Surface.CurrentMatch = vm is { SelectedRowIndex: int r, SelectedColumnIndex: int c, SelectedMatchOccurrence: int o }
                ? new TableMatch(r, c, o)
                : null;
            if (vm.SelectedRowIndex is int row)
                Surface.RevealRow(row);
        }

        bool columnChanged = e.PropertyName is null or nameof(CsvViewModel.SelectedColumnIndex);
        if (columnChanged && vm.SelectedColumnIndex is int column)
            Surface.RevealColumn(column);

        if (e.PropertyName == nameof(CsvViewModel.RowShownAtTop) && vm.RowShownAtTop is int top)
            Surface.ShowRowAtTop(top);
    }

    private void ShowDocument(CsvViewModel vm)
    {
        // Rows and Structure throw until LoadAsync has published them.
        if (vm.ColumnCount == 0)
            return;

        Surface.Rows = vm.Rows;
        Surface.FitSource = vm.Rows;
        Surface.Structure = vm.Structure;
        this.scrollBars.Refresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        Surface.PropertyChanged -= OnSurfacePropertyChanged;
        Surface.ScrollPositionChanged -= OnSurfaceScrolled;

        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            this.subscribedViewModel = null;
        }

        // The surface is the last thing reading the row collection.
        Surface.Rows = null;
        this.scrollBars.Dispose();

        // Disposed synchronously here: CsvRowCollection reports empty once disposed.
        if (DataContext is IDisposable d)
            d.Dispose();
    }
}
