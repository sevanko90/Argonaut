using System;
using System.ComponentModel;
using Avalonia.Controls;

namespace Argonaut.Features.NdJson;

public partial class NdJsonView : UserControl
{
    private bool suppressSelectionEvents;
    private NdJsonViewModel? subscribedViewModel;

    public NdJsonView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        JsonLinesListBox.SelectionChanged += OnSelectionChanged;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is NdJsonViewModel vm)
            SyncVisualSelection(vm);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        if (DataContext is NdJsonViewModel vm)
        {
            subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// Mirrors programmatic selection (e.g. a search reveal calling LoadSelectedLine) into
    /// the ListBox - user clicks already go the other way via OnSelectionChanged, and
    /// SyncVisualSelection suppresses the echo so LoadSelectedLine isn't re-entered.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NdJsonViewModel vm)
            return;

        if (e.PropertyName is null or nameof(NdJsonViewModel.SelectedLineNumber))
            SyncVisualSelection(vm);
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        JsonLinesListBox.SelectionChanged -= OnSelectionChanged;
        DataContextChanged -= OnDataContextChanged;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

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
