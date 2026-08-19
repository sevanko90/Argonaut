using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Infrastructure;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Features.Json.Diff;

public partial class JsonDiffView : UserControl
{
    private bool suppressSelectionEvents;
    private JsonDiffViewModel? subscribedViewModel;
    private JsonDiffRowCollection? subscribedRows;

    public JsonDiffView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        RowsListBox.SelectionChanged += OnSelectionChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        if (DataContext is JsonDiffViewModel vm)
        {
            subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;

            subscribedRows = vm.Rows;
            subscribedRows.CollectionChanged += OnRowsCollectionChanged;
        }
    }

    private void Unsubscribe()
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        if (subscribedRows is not null)
        {
            subscribedRows.CollectionChanged -= OnRowsCollectionChanged;
            subscribedRows = null;
        }
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        RowsListBox.SelectionChanged -= OnSelectionChanged;
        DataContextChanged -= OnDataContextChanged;
        Unsubscribe();

        // Disposed synchronously here (before the content swap's trailing ItemsSource
        // walk), same as JsonView: the collection reports empty once disposed, so that
        // walk reads nothing. Idempotent alongside the shell's own dispose.
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    /// <summary>The model drives the visual selection (next/previous-diff buttons land
    /// here); the guard stops the resulting SelectionChanged echoing back into the model.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(JsonDiffViewModel.SelectedPosition)))
            return;

        SyncVisualSelection();
    }

    private void SyncVisualSelection()
    {
        if (subscribedViewModel is not { } vm)
            return;

        int index = vm.SelectedPosition is { } p && p >= 0 && p < (subscribedRows?.Count ?? 0) ? p : -1;
        if (RowsListBox.SelectedIndex == index)
            return;

        suppressSelectionEvents = true;
        try
        {
            RowsListBox.SelectedIndex = index;
            if (index >= 0)
                RowsListBox.ScrollIntoView(index);
        }
        finally
        {
            suppressSelectionEvents = false;
        }
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Every rebuild fires a Reset, which clears the ListBox's selection; restore it
        // from the model a dispatcher turn later, after all subscribers have consumed the
        // Reset (same deferral - and reasoning - as JsonView.OnRowsCollectionChanged).
        Dispatcher.UIThread.Post(SyncVisualSelection);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectionEvents)
            return;

        if (subscribedViewModel is not { } vm)
            return;

        vm.SelectedPosition = RowsListBox.SelectedIndex >= 0 ? RowsListBox.SelectedIndex : null;
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

    private void OnToggleSourceMode(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is JsonDiffViewModel vm)
            vm.ToggleSourceMode();
    }

    private void OnToggleTargetMode(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is JsonDiffViewModel vm)
            vm.ToggleTargetMode();
    }

    private async void OnCopySourceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not JsonDiffViewModel vm)
            return;

        await CopyToClipboardAsync(vm.SourcePrefix + vm.SourceChanged + vm.SourceSuffix);
    }

    private async void OnCopyTargetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not JsonDiffViewModel vm)
            return;

        await CopyToClipboardAsync(vm.TargetPrefix + vm.TargetChanged + vm.TargetSuffix);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(text);
        ToastService.Show("Copied to clipboard");
    }
}
