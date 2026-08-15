using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Features.Json;

public partial class JsonView : UserControl
{
    private bool suppressSelectionEvents;
    private JsonVisibleRowCollection? subscribedRows;
    private JsonViewModel? subscribedViewModel;
    private MenuFlyout? hintFlyout;
    private int hintFlyoutTokenIndex = -1;
    private KeyModifiers lastRowsPressModifiers;

    /// <summary>How wide the schema gutter opens, in px. Seeded with a default and thereafter
    /// whatever the user last dragged it to, remembered across schema changes within this view so
    /// unbinding and rebinding a schema doesn't discard a deliberate resize. Not persisted.</summary>
    private double schemaGutterWidth = DefaultSchemaGutterWidth;
    private const double DefaultSchemaGutterWidth = 220;
    private const double MinSchemaGutterWidth = 40;
    private bool schemaGutterShown;

    private ScrollViewer? rowsScrollViewer;
    private ScrollViewer? gutterScrollViewer;
    private bool syncingScroll;
    private JsonSchemaSettings? subscribedSchemaSettings;

    public JsonView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        RowsListBox.SelectionChanged += OnSelectionChanged;

        // Tunnel-stage capture only, and never marks Handled: Button.Click carries no
        // modifiers, so remember the press modifiers for OnToggleExpandClick (alt/option
        // on the expander = deep toggle).
        RowsListBox.AddHandler(PointerPressedEvent, OnRowsListPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Both ListBoxes' ScrollViewers come from their control themes, so they don't exist until
    /// the visual tree is built - same lazy resolution CsvView uses for its sticky header.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (rowsScrollViewer is not null)
            return;

        rowsScrollViewer = RowsListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        gutterScrollViewer = SchemaGutterListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        if (rowsScrollViewer is not null)
            rowsScrollViewer.ScrollChanged += OnRowsScrollChanged;
        if (gutterScrollViewer is not null)
            gutterScrollViewer.ScrollChanged += OnGutterScrollChanged;
    }

    // Mirrored in both directions so the wheel works over the gutter too. The gutter's viewport is
    // slightly taller than the tree's whenever the tree shows a horizontal scrollbar, so at the
    // very bottom the gutter can hold an offset the tree clamps away; the return mirror pulls the
    // gutter back to the clamped value, which converges (offsets only ever shrink) in one step.
    private void OnRowsScrollChanged(object? sender, ScrollChangedEventArgs e) => MirrorVerticalOffset(rowsScrollViewer, gutterScrollViewer);

    private void OnGutterScrollChanged(object? sender, ScrollChangedEventArgs e) => MirrorVerticalOffset(gutterScrollViewer, rowsScrollViewer);

    private void MirrorVerticalOffset(ScrollViewer? from, ScrollViewer? to)
    {
        if (syncingScroll || from is null || to is null || from.Offset.Y == to.Offset.Y)
            return;

        syncingScroll = true;
        try
        {
            to.Offset = new Vector(to.Offset.X, from.Offset.Y);
        }
        finally
        {
            syncingScroll = false;
        }
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        RowsListBox.RemoveHandler(PointerPressedEvent, OnRowsListPointerPressed);
        RowsListBox.SelectionChanged -= OnSelectionChanged;
        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;

        if (rowsScrollViewer is not null)
        {
            rowsScrollViewer.ScrollChanged -= OnRowsScrollChanged;
            rowsScrollViewer = null;
        }

        if (gutterScrollViewer is not null)
        {
            gutterScrollViewer.ScrollChanged -= OnGutterScrollChanged;
            gutterScrollViewer = null;
        }

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

            subscribedSchemaSettings = vm.SchemaSettings;
            subscribedSchemaSettings.PropertyChanged += OnSchemaSettingsPropertyChanged;

            if (TryGetRows(vm, out var rows))
            {
                subscribedRows = rows;
                rows.CollectionChanged += OnRowsCollectionChanged;
            }
        }

        ApplySchemaGutterVisibility();
        SyncVisualSelection();
    }

    private void UnsubscribeViewModel()
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        if (subscribedSchemaSettings is not null)
        {
            subscribedSchemaSettings.PropertyChanged -= OnSchemaSettingsPropertyChanged;
            subscribedSchemaSettings = null;
        }

        if (subscribedRows is null)
            return;

        subscribedRows.CollectionChanged -= OnRowsCollectionChanged;
        subscribedRows = null;
    }

    private void OnSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(JsonSchemaSettings.Document))
            ApplySchemaGutterVisibility();
    }

    /// <summary>
    /// Opens the schema gutter to its remembered width while a schema is bound, and closes it to
    /// zero otherwise - an empty gutter would be a few hundred px of dead space on the far more
    /// common no-schema document. Collapsing the *column* rather than hiding the ListBox keeps the
    /// gutter realized, so its internal ScrollViewer (resolved once, on Loaded) stays valid and
    /// the scroll mirroring survives a schema being unbound and rebound.
    /// </summary>
    private void ApplySchemaGutterVisibility()
    {
        bool show = subscribedViewModel?.SchemaSettings.Document is not null;
        if (show == schemaGutterShown)
            return;

        // Read the user's drag back out before collapsing, or reopening would snap to the default.
        if (!show)
            schemaGutterWidth = Math.Max(MinSchemaGutterWidth, SchemaGutterGrid.ColumnDefinitions[0].Width.Value);

        schemaGutterShown = show;
        SchemaGutterGrid.ColumnDefinitions[0].Width = new GridLength(show ? schemaGutterWidth : 0);
        SchemaGutterSplitter.IsVisible = show;
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
        // A pure tail-append (JsonVisibleRowCollection.Rebuild's isPureAppend path) can
        // never move any row that was already visible, including the selected one - only
        // new rows appear after it. There's nothing to resync, and forcing one anyway means
        // reassigning RowsListBox.SelectedIndex to the *same* value on every growth-poll
        // tick while a big file indexes (up to 4x/second) - avoidable selection/scroll work
        // on a virtualizing panel that Avalonia doesn't handle cleanly under that much
        // churn (e.g. https://github.com/AvaloniaUI/Avalonia/issues/11666,
        // https://github.com/AvaloniaUI/Avalonia/issues/17635).
        if (e.Action == NotifyCollectionChangedAction.Add)
            return;

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

        // Consume the captured press modifiers so a keyboard-activated Click (Space/Enter)
        // can't reuse a stale alt from an earlier pointer press.
        bool deepToggle = (lastRowsPressModifiers & KeyModifiers.Alt) != 0;
        lastRowsPressModifiers = KeyModifiers.None;

        if (deepToggle)
        {
            if (vm.Rows.ToggleExpandAll(row.Position))
                ToastService.Show("Expanded to the display limit");
        }
        else
        {
            vm.Rows.ToggleExpand(row.Position);
        }
    }

    private void OnRowsListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        lastRowsPressModifiers = e.KeyModifiers;
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The expand button and the hint-scheme button already handle their own Click twice
        // over during a double-tap (net no-op for the expander, opens the flyout for hint) -
        // don't also toggle here or the expander would flip a third time.
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
            return;

        OnToggleExpandClick(sender, e);
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

    private void OnJumpToRawClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: JsonRow { TruncatedValueOffset: { } offset } })
            return;

        RawJumpService.Request(offset);
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

        // Reassigning SelectedIndex to the value it already holds is not a no-op as far as
        // the ListBox is concerned - it still redoes selection/scroll bookkeeping. Guard it
        // so a caller that couldn't already tell nothing changed (e.g. a growth tick whose
        // Reset fallback path re-derives the same position) doesn't pay for that anyway.
        if (RowsListBox.SelectedIndex == index)
            return;

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
