using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Argonaut.Features.Csv;

public partial class CsvView : UserControl
{
    private ScrollViewer? bodyScrollViewer;

    public CsvView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
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
        DetachedFromVisualTree -= OnDetachedFromVisualTree;

        if (bodyScrollViewer is not null)
        {
            bodyScrollViewer.ScrollChanged -= OnBodyScrollChanged;
            bodyScrollViewer = null;
        }

        if (DataContext is IDisposable d)
            d.Dispose();
    }
}
