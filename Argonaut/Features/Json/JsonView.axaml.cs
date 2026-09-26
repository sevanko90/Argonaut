using System;
using System.ComponentModel;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Argonaut.Features.Json;

/// <summary>
/// The JSON tree: a <see cref="TreeSurface"/> over the view model's <see cref="JsonViewModel.Tree"/>,
/// a pan scrollbar under it, and the path bar. Everything a row does beyond selecting and
/// expanding arrives as a link the surface raises - a date hint's scheme menu, the jump to the raw
/// view for a truncated value, "view as table" - and is acted on here.
/// </summary>
public partial class JsonView : UserControl
{
    private JsonViewModel? subscribedViewModel;
    private MenuFlyout? hintFlyout;
    private long hintFlyoutValueOffset = -1;

    private readonly RowScrollBars scrollBars;

    public JsonView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        ActualThemeVariantChanged += OnThemeChanged;

        Surface.SelectionChanged += OnSurfaceSelectionChanged;
        Surface.LinkClicked += OnLinkClicked;
        Surface.ExpandLimitReached += OnExpandLimitReached;
        Surface.SizeChanged += OnSurfaceSizeChanged;
        scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);

        // Right-click copies the row's value. The surface has already selected the row on the
        // press, so the release copies what is now selected.
        Surface.AddHandler(PointerReleasedEvent, OnSurfacePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyRunBrushes();
        scrollBars.Refresh();
        ApplyPendingReveal();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyRunBrushes();

    /// <summary>The row palette, from the theme - again whenever the theme changes.</summary>
    private void ApplyRunBrushes() => JsonTreePalette.Apply(Surface, this);

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Loaded -= OnLoaded;
        DataContextChanged -= OnDataContextChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        Surface.SelectionChanged -= OnSurfaceSelectionChanged;
        Surface.LinkClicked -= OnLinkClicked;
        Surface.ExpandLimitReached -= OnExpandLimitReached;
        Surface.SizeChanged -= OnSurfaceSizeChanged;
        scrollBars.Dispose();
        Surface.RemoveHandler(PointerReleasedEvent, OnSurfacePointerReleased);

        UnsubscribeViewModel();

        // Let go of the tree before the view model releases the bytes it reads.
        Surface.Document = null;
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
            vm.RevealRequested += OnRevealRequested;
            vm.RowsInvalidated += OnRowsInvalidated;
            vm.ExpansionReset += OnExpansionReset;
            Surface.Document = vm.Tree;
            Surface.HighlightTerm = vm.HighlightTerm;
            ApplyPendingReveal();
        }
        else
        {
            Surface.Document = null;
        }

        scrollBars.Refresh();
    }

    private void UnsubscribeViewModel()
    {
        if (subscribedViewModel is null)
            return;

        subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        subscribedViewModel.RevealRequested -= OnRevealRequested;
        subscribedViewModel.RowsInvalidated -= OnRowsInvalidated;
        subscribedViewModel.ExpansionReset -= OnExpansionReset;
        subscribedViewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JsonViewModel vm)
            return;

        if (e.PropertyName is null or nameof(JsonViewModel.HighlightTerm))
            Surface.HighlightTerm = vm.HighlightTerm;
    }

    private void OnRevealRequested(object? sender, EventArgs e) => ApplyPendingReveal();

    /// <summary>Shows the view model's pending reveal once the surface can: it needs a document
    /// and a height to centre the row in.</summary>
    private void ApplyPendingReveal()
    {
        if (subscribedViewModel is not { PendingReveal: { } offset } vm || Surface.Document is null || Surface.Bounds.Height <= 0)
            return;

        vm.ClearPendingReveal();
        Surface.Reveal(offset, expandAncestors: true);
    }

    private void OnRowsInvalidated(object? sender, EventArgs e)
    {
        Surface.InvalidateRows();
        Surface.InvalidateGutters();
        scrollBars.Refresh();
    }

    private void OnExpansionReset(object? sender, EventArgs e) => Surface.Reseat();

    private void OnSurfaceSelectionChanged(object? sender, EventArgs e)
        => subscribedViewModel?.OnRowSelected(Surface.SelectedRow);

    private void OnExpandLimitReached(object? sender, EventArgs e) => ToastService.Show("Expanded to the display limit");

    private void OnLinkClicked(object? sender, TreeLinkClickedEventArgs e)
    {
        switch (e.Link)
        {
            case ViewInRawLink raw:
                // The link's offset is in this document's bytes; an NDJSON line's start puts it in the file's.
                RawJumpService.Request(ByteRange.At((subscribedViewModel?.ScanTarget.Offset ?? 0) + raw.Offset));
                break;
            case ViewAsTableLink table:
                subscribedViewModel?.RequestArrayTable(table.ArrayStart);
                break;
            case DateSchemeLink hint:
                hintFlyoutValueOffset = hint.ValueOffset;
                (hintFlyout ??= BuildHintFlyout()).ShowAt(Surface, showAtPointer: true);
                break;
        }
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
            if (DataContext is JsonViewModel vm && hintFlyoutValueOffset >= 0)
                vm.HintSettings.SetValueOverride(hintFlyoutValueOffset, scheme);
        };
        flyout.Items.Add(item);
    }

    private void OnPathSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: JsonTreePathSegment segment } && DataContext is JsonViewModel vm)
            vm.Reveal(segment.Target);
    }

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JsonViewModel { SelectedPath: { } path })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(path);

        ToastService.Show("JSONPath copied to clipboard");
    }

    private async void OnCopyValueClick(object? sender, RoutedEventArgs e) => await CopySelectedValueAsync();

    private async void OnSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
            return;

        e.Handled = true;
        await CopySelectedValueAsync();
    }

    private async System.Threading.Tasks.Task CopySelectedValueAsync()
    {
        if (DataContext is not JsonViewModel { SelectedValueText: { } value })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(value);
        ToastService.Show("Value copied to clipboard");
    }

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyPendingReveal();
}
