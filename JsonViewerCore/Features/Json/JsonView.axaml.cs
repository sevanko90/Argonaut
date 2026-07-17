using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace JsonViewerCore.Features.Json;

public partial class JsonView : UserControl
{
    private bool suppressSelectionEvents;
    private JsonVisibleRowCollection? subscribedRows;

    public JsonView()
    {
        InitializeComponent();

        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        RowsListBox.SelectionChanged += OnSelectionChanged;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        RowsListBox.SelectionChanged -= OnSelectionChanged;
        DataContextChanged -= OnDataContextChanged;
        UnsubscribeRows();

        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeRows();

        if (DataContext is JsonViewModel vm && TryGetRows(vm, out var rows))
        {
            subscribedRows = rows;
            rows.CollectionChanged += OnRowsCollectionChanged;
        }

        SyncVisualSelection();
    }

    private void UnsubscribeRows()
    {
        if (subscribedRows is null)
            return;

        subscribedRows.CollectionChanged -= OnRowsCollectionChanged;
        subscribedRows = null;
    }

    private static bool TryGetRows(JsonViewModel vm, out JsonVisibleRowCollection rows)
    {
        // Rows throws until LoadAsync completes; DataContext is only ever assigned to a
        // fully-loaded JsonViewModel by MainWindow/NdJsonView, but guard anyway.
        try
        {
            rows = vm.Rows;
            return true;
        }
        catch (InvalidOperationException)
        {
            rows = null!;
            return false;
        }
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncVisualSelection();
    }

    private void OnToggleExpandClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: JsonRow row })
            return;

        if (DataContext is not JsonViewModel vm)
            return;

        // Select before toggling: ToggleExpand rebuilds the visible row list synchronously,
        // and that rebuild is what restores the ListBox's visual selection (via
        // OnRowsCollectionChanged) - so the model needs to already point at this token when
        // that happens. Opening/closing a node now also selects it, so the highlighted node
        // stays visible instead of the selection appearing to vanish when the list resets.
        if (!row.IsPlaceholder)
            vm.SelectToken(row.TokenIndex);

        vm.Rows.ToggleExpand(row.Position);
    }

    private void OnPathSegmentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: JsonPathSegment segment })
            return;

        if (DataContext is not JsonViewModel vm)
            return;

        vm.SelectToken(segment.TokenIndex);

        // Safety net for the common case where the target was already fully visible:
        // EnsureVisible then makes no change and never raises CollectionChanged, so
        // nothing else would re-derive the ListBox's highlight/autoscroll from the new
        // SelectedTokenIndex. When EnsureVisible does rebuild, this call is idempotent -
        // OnRowsCollectionChanged already invoked it with the same result.
        SyncVisualSelection();
    }

    private async void OnCopyPathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not JsonViewModel { SelectedPath: { } path })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(path);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectionEvents)
            return;

        if (DataContext is not JsonViewModel vm)
            return;

        // Placeholder ("N more items") and closing-bracket rows aren't valid JSONPath
        // targets - the closing bracket shares its token's parent/depth with its Start
        // token but carries no name info of its own and no cheap link back to it, so fall
        // back to whatever the model's current selection is instead of computing one.
        if (RowsListBox.SelectedItem is not JsonRow { IsPlaceholder: false } row ||
            row.Kind is JsonTokenKind.EndObject or JsonTokenKind.EndArray)
        {
            SyncVisualSelection();
            return;
        }

        vm.SelectToken(row.TokenIndex);
    }

    /// <summary>
    /// Re-derives the ListBox's visual selection from the model's SelectedTokenIndex.
    /// Needed because JsonVisibleRowCollection.Rebuild fires a Reset on every
    /// expand/collapse (rows.axaml's ListBox clears SelectedIndex on any Reset), and a
    /// token's row position shifts across rebuilds, so the raw ListBox index can't be
    /// trusted to survive one - only the token identity can.
    /// </summary>
    private void SyncVisualSelection()
    {
        int index = -1;
        if (DataContext is JsonViewModel { SelectedTokenIndex: { } tokenIndex } && subscribedRows is not null)
            index = subscribedRows.FindVisiblePosition(tokenIndex) ?? -1;

        suppressSelectionEvents = true;
        try
        {
            RowsListBox.SelectedIndex = index;
        }
        finally
        {
            suppressSelectionEvents = false;
        }
    }
}
