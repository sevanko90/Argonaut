using System;
using Avalonia.Controls;

namespace JsonViewerCore.Features.Json;

public partial class JsonView : UserControl
{
    public JsonView()
    {
        InitializeComponent();

        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnToggleExpandClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: JsonRow row })
            return;

        if (DataContext is not JsonViewModel vm)
            return;

        vm.Rows.ToggleExpand(row.Position);
    }
}
