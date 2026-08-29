using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Argonaut.Features.Json;

/// <summary>
/// The array-table grid. Same two jobs CsvView's code-behind has: find the ListBox's own
/// internal ScrollViewer once the visual tree exists and mirror its horizontal offset onto the
/// sticky header, and dispose the document on detach as the idempotent safety net the shell
/// doesn't drive (window close).
///
/// No column scroll-into-view counterpart, because that exists for search reveals and this
/// document has no search navigator in v1.
/// </summary>
public partial class JsonArrayTableView : UserControl
{
    private ScrollViewer? bodyScrollViewer;

    public JsonArrayTableView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

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

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Loaded -= OnLoaded;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;

        if (bodyScrollViewer is not null)
        {
            bodyScrollViewer.ScrollChanged -= OnBodyScrollChanged;
            bodyScrollViewer = null;
        }

        // Disposed synchronously here (before the content swap's trailing ItemsSource walk):
        // JsonArrayRowCollection reports empty once disposed, so that walk reads nothing.
        if (DataContext is IDisposable d)
            d.Dispose();
    }
}
