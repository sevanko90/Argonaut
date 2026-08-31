using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.VisualTree;

namespace Argonaut.Features.Csv;

/// <summary>
/// The CSV grid's code-behind. Three jobs: keep the TableView's columns in step with the view
/// model's <see cref="CsvViewModel.Structure"/> (the "first row is header" tickbox relabels
/// them), reveal what a search match selected, and dispose the document on detach as the
/// idempotent safety net the shell doesn't drive (window close).
///
/// The sticky header, its horizontal-scroll tracking and the column resizer all belong to
/// TableView now; what used to be mirrored by hand here is gone.
/// </summary>
public partial class CsvView : UserControl
{
    private readonly TableGridColumns columns;
    private CsvViewModel? subscribedViewModel;

    public CsvView()
    {
        InitializeComponent();

        this.columns = new TableGridColumns(Table);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.subscribedViewModel is not null)
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        this.subscribedViewModel = DataContext as CsvViewModel;
        if (this.subscribedViewModel is null)
            return;

        this.subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildColumns(this.subscribedViewModel);
    }

    /// <summary>
    /// Reveals a search match (CsvSearchNavigator.SelectRow): the row vertically via
    /// TableView.SelectedIndex (Avalonia auto-scrolls the selected item into view, no explicit
    /// ScrollIntoView needed - same as NdJsonView), the column horizontally via the grid's own
    /// ScrollViewer, which the navigator/view model never touch directly.
    ///
    /// A new structure means new column labels (the tickbox) - the widths are unchanged, but the
    /// columns carry the names, so they are rebuilt from it.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CsvViewModel vm)
            return;

        if (e.PropertyName is null or nameof(CsvViewModel.Structure))
            RebuildColumns(vm);

        if (e.PropertyName is null or nameof(CsvViewModel.SelectedRowIndex))
            Table.SelectedIndex = vm.SelectedRowIndex ?? -1;

        bool columnChanged = e.PropertyName is null or nameof(CsvViewModel.SelectedColumnIndex);
        if (columnChanged && vm.SelectedColumnIndex is int columnIndex)
            ScrollColumnIntoView(columnIndex);
    }

    private void RebuildColumns(CsvViewModel vm)
    {
        // Structure throws until LoadAsync has published one.
        if (vm.ColumnCount == 0)
            return;

        this.columns.Rebuild(vm.Structure, vm.Rows, new Binding(nameof(CsvViewModel.HighlightTerm)) { Source = vm });
    }

    /// <summary>
    /// Scrolls the grid horizontally just enough to bring [left, left + width) for
    /// <paramref name="columnIndex"/> fully into the viewport - standard scroll-into-view clamp,
    /// only moving when the target isn't already visible. Widths come from the columns' own
    /// ActualWidth, so a column the user resized is measured as it currently is.
    /// </summary>
    private void ScrollColumnIntoView(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= Table.Columns.Count)
            return;

        if (Table.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
            return;

        double left = 0;
        for (int i = 0; i < columnIndex; i++)
            left += Table.Columns[i].ActualWidth;
        double right = left + Table.Columns[columnIndex].ActualWidth;

        double viewportLeft = scroll.Offset.X;
        double viewportRight = viewportLeft + scroll.Viewport.Width;

        double newLeft = viewportLeft;
        if (left < viewportLeft)
            newLeft = left;
        else if (right > viewportRight)
            newLeft = Math.Max(0, right - scroll.Viewport.Width);

        if (newLeft != viewportLeft)
            scroll.Offset = new Vector(newLeft, scroll.Offset.Y);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;

        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            this.subscribedViewModel = null;
        }

        // Columns first: their cell bindings are the last things reading the row collection.
        this.columns.Dispose();

        // Disposed synchronously here (before the content swap's trailing ItemsSource walk):
        // CsvRowCollection reports empty once disposed, so that walk reads nothing.
        if (DataContext is IDisposable d)
            d.Dispose();
    }
}
