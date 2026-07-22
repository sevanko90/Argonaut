using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Argonaut.Features.Json;

public partial class JsonView : UserControl
{
    private bool suppressSelectionEvents;
    private JsonVisibleRowCollection? subscribedRows;
    private JsonViewModel? subscribedViewModel;
    private MenuFlyout? hintFlyout;
    private int hintFlyoutTokenIndex = -1;

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
        UnsubscribeViewModel();

        // Disposed synchronously here (before the content swap's trailing ItemsSource walk):
        // JsonVisibleRowCollection reports empty once disposed, so that walk reads nothing.
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeViewModel();

        if (DataContext is JsonViewModel vm)
        {
            subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;

            if (TryGetRows(vm, out var rows))
            {
                subscribedRows = rows;
                rows.CollectionChanged += OnRowsCollectionChanged;
            }
        }

        SyncVisualSelection();
    }

    private void UnsubscribeViewModel()
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        if (subscribedRows is null)
            return;

        subscribedRows.CollectionChanged -= OnRowsCollectionChanged;
        subscribedRows = null;
    }

    /// <summary>
    /// Any SelectToken caller (breadcrumb click, search reveal, nested NDJSON reveal) syncs
    /// the ListBox highlight/autoscroll through this, covering the case where EnsureVisible
    /// changed nothing and so no CollectionChanged Reset ever fires.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(JsonViewModel.SelectedTokenIndex))
            SyncVisualSelection();
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
        // Deliberately deferred (not the banned marshal-after-await pattern): never set
        // RowsListBox.SelectedIndex from inside the rows collection's own CollectionChanged.
        // Subscriber order vs the ListBox's ItemsSourceView is unspecified, and when this
        // handler runs first the selection model still holds its pre-rebuild indexes; setting
        // SelectedIndex makes it materialise those against the already-rebuilt (possibly
        // shorter) list - ArgumentOutOfRangeException from GetRow, and the failed commit
        // leaves the model stuck with the stale index so every later rebuild re-throws.
        // Posting runs the sync after all subscribers have consumed the Reset, when the
        // ListBox has already dropped the stale selection.
        Dispatcher.UIThread.Post(SyncVisualSelection);
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

        // OnViewModelPropertyChanged re-derives the ListBox highlight/autoscroll from the
        // SelectedTokenIndex change, whether or not EnsureVisible rebuilt the row list.
        vm.SelectToken(segment.TokenIndex);
    }

    private void OnHintClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: JsonRow { Hint: not null } row } control)
            return;

        hintFlyoutTokenIndex = row.TokenIndex;
        (hintFlyout ??= BuildHintFlyout()).ShowAt(control);
    }

    private MenuFlyout BuildHintFlyout()
    {
        var flyout = new MenuFlyout();
        AddHintSchemeItem(flyout, "File default", null);
        AddHintSchemeItem(flyout, "Off", DateDecodingScheme.Off);
        AddHintSchemeItem(flyout, "JS milliseconds", DateDecodingScheme.JsMilliseconds);
        AddHintSchemeItem(flyout, "JS seconds", DateDecodingScheme.JsSeconds);
        AddHintSchemeItem(flyout, "Keepa minutes", DateDecodingScheme.KeepaMinutes);
        return flyout;
    }

    private void AddHintSchemeItem(MenuFlyout flyout, string header, DateDecodingScheme? scheme)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            if (DataContext is JsonViewModel vm && hintFlyoutTokenIndex >= 0)
                vm.HintSettings.SetTokenOverride(hintFlyoutTokenIndex, scheme);
        };
        flyout.Items.Add(item);
    }

    private async void OnCopyPathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not JsonViewModel { SelectedPath: { } path })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(path);

        ToastService.Show("JSONPath copied to clipboard");
    }

    private async void OnCopyValueClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (RowsListBox.SelectedItem is not JsonRow { IsPlaceholder: false } row)
            return;

        await CopyValueToClipboardAsync(row);
        ToastService.Show("Value copied to clipboard");
    }

    private async void OnRowPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: JsonRow { IsPlaceholder: false } row })
            return;

        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
            return;

        e.Handled = true;
        await CopyValueToClipboardAsync(row);
        ToastService.Show("Value copied to clipboard");
    }

    private async Task CopyValueToClipboardAsync(JsonRow row)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        // Value carries the display formatting (e.g. quoted strings); strip the quotes so
        // the clipboard holds the raw value rather than a JSON-literal rendering of it.
        string text = row.Kind == JsonTokenKind.String && row.Value.Length >= 2
            ? row.Value[1..^1]
            : row.Value;

        await clipboard.SetTextAsync(text);
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
