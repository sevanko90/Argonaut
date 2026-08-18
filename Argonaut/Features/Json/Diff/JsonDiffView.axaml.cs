using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Argonaut.Features.Json.Diff;

public partial class JsonDiffView : UserControl
{
    public JsonDiffView()
    {
        InitializeComponent();

        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // Disposed synchronously here (before the content swap's trailing ItemsSource
        // walk), same as JsonView: the collection reports empty once disposed, so that
        // walk reads nothing. Idempotent alongside the shell's own dispose.
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnToggleExpandClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: JsonDiffRow row })
            return;

        if (DataContext is not JsonDiffViewModel vm)
            return;

        vm.Rows.ToggleExpand(row.Position);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The expander button already handled its own two clicks; don't triple-toggle.
        if (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<Button>() is not null)
            return;

        OnToggleExpandClick(sender, e);
    }
}
