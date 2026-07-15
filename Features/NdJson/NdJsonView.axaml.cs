using System;
using Avalonia.Controls;

namespace JsonViewerCore.Features.NdJson;

public partial class NdJsonView : UserControl
{
    public NdJsonView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateWindow();
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateWindow();
    }

    private void UpdateWindow()
    {
        if (DataContext is not NdJsonViewModel vm)
            return;

        const double lineHeight = 20;
        var viewportLines = (int)Math.Ceiling(MainScrollViewer.Viewport.Height / lineHeight);
        if (viewportLines <= 0)
            viewportLines = 50;

        var firstVisibleIndex = Math.Max(0, (int)Math.Floor(MainScrollViewer.Offset.Y / lineHeight));
        vm.EnsureWindow(firstVisibleIndex, viewportLines);
    }
}
