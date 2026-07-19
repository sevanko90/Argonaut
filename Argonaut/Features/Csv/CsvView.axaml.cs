using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Argonaut.Features.Csv;

public partial class CsvView : UserControl
{
    private ScrollViewer? bodyScrollViewer;
    private CsvViewModel? subscribedViewModel;

    public CsvView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        subscribedViewModel = DataContext as CsvViewModel;
        if (subscribedViewModel is not null)
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Reveals a search match (CsvSearchNavigator.SelectRow): the row vertically via
    /// ListBox.SelectedIndex (Avalonia auto-scrolls the selected item into view, no explicit
    /// ScrollIntoView needed - same as NdJsonView), the column horizontally via the grid's own
    /// ScrollViewer, which the navigator/view model never touch directly.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CsvViewModel vm)
            return;

        if (e.PropertyName is null or nameof(CsvViewModel.SelectedRowIndex))
            RowsListBox.SelectedIndex = vm.SelectedRowIndex ?? -1;

        bool columnChanged = e.PropertyName is null or nameof(CsvViewModel.SelectedColumnIndex);
        if (columnChanged && vm.SelectedColumnIndex is int columnIndex)
            ScrollColumnIntoView(columnIndex, vm.ColumnLayout);
    }

    /// <summary>
    /// Scrolls the body ScrollViewer horizontally just enough to bring
    /// [left, left + width) for <paramref name="columnIndex"/> fully into the viewport -
    /// standard scroll-into-view clamp, only moving when the target isn't already visible.
    /// Setting the offset here also keeps the sticky header aligned for free, via the existing
    /// OnBodyScrollChanged mirroring.
    /// </summary>
    private void ScrollColumnIntoView(int columnIndex, CsvColumnLayout layout)
    {
        if (bodyScrollViewer is null || columnIndex < 0)
            return;

        double left = 0;
        for (int i = 0; i < columnIndex && i < layout.Widths.Count; i++)
            left += layout.Widths[i];
        double width = columnIndex < layout.Widths.Count ? layout.Widths[columnIndex] : 0;
        double right = left + width;

        double viewportLeft = bodyScrollViewer.Offset.X;
        double viewportWidth = bodyScrollViewer.Viewport.Width;
        double viewportRight = viewportLeft + viewportWidth;

        double newLeft = viewportLeft;
        if (left < viewportLeft)
            newLeft = left;
        else if (right > viewportRight)
            newLeft = Math.Max(0, right - viewportWidth);

        if (newLeft != viewportLeft)
            bodyScrollViewer.Offset = new Vector(newLeft, bodyScrollViewer.Offset.Y);
    }

    /// <summary>
    /// Finds the ListBox's own internal ScrollViewer (created by its control theme, so it
    /// isn't available until the visual tree is built) and mirrors its horizontal offset onto
    /// the sticky header's ScrollViewer, keeping columns aligned as the body scrolls sideways.
    /// </summary>
    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (bodyScrollViewer is not null)
            return;

        bodyScrollViewer = RowsListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (bodyScrollViewer is not null)
            bodyScrollViewer.ScrollChanged += OnBodyScrollChanged;
    }

    private void OnBodyScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (bodyScrollViewer is null)
            return;

        HeaderScrollViewer.Offset = new Vector(bodyScrollViewer.Offset.X, 0);
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Loaded -= OnLoaded;
        DataContextChanged -= OnDataContextChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        if (bodyScrollViewer is not null)
        {
            bodyScrollViewer.ScrollChanged -= OnBodyScrollChanged;
            bodyScrollViewer = null;
        }

        if (DataContext is IDisposable d)
            d.Dispose();
    }
}
