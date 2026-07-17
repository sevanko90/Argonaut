using System;
using Avalonia.Controls;

namespace JsonViewerCore.Features.NdJson;

public partial class NdJsonView : UserControl
{
    private bool suppressSelectionEvents;

    public NdJsonView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        JsonLinesListBox.SelectionChanged += OnSelectionChanged;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is NdJsonViewModel vm)
            SyncVisualSelection(vm);
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        JsonLinesListBox.SelectionChanged -= OnSelectionChanged;

        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectionEvents)
            return;

        if (DataContext is not NdJsonViewModel vm)
            return;

        var selectedIndex = JsonLinesListBox.SelectedIndex;
        if (selectedIndex < 0)
            return;

        vm.LoadSelectedLine(selectedIndex);
    }

    private void SyncVisualSelection(NdJsonViewModel vm)
    {
        if (vm.SelectedLineNumber is null)
        {
            JsonLinesListBox.SelectedIndex = -1;
            return;
        }

        suppressSelectionEvents = true;
        try
        {
            var selectedIndex = vm.SelectedLineNumber.Value - 1;
            JsonLinesListBox.SelectedIndex = selectedIndex >= 0 && selectedIndex < vm.LineCount
                ? selectedIndex
                : -1;
        }
        finally
        {
            suppressSelectionEvents = false;
        }
    }
}
