using System;
using System.Collections.Generic;
using System.ComponentModel;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Notifications;
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
        Surface.WidestRowWidthChanged += OnWidestRowWidthChanged;
        Surface.PanRequested += OnPanRequested;
        Surface.SizeChanged += OnSurfaceSizeChanged;
        PanScrollBar.ValueChanged += OnPanValueChanged;

        // Right-click copies the row's value. The surface has already selected the row on the
        // press, so the release copies what is now selected.
        Surface.AddHandler(PointerReleasedEvent, OnSurfacePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyRunBrushes();
        UpdatePanRange();
        ApplyPendingReveal();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyRunBrushes();

    /// <summary>
    /// The row palette, from the theme: the same brushes the row template used to bind, looked up
    /// again whenever the theme changes. A run style with no brush here draws in the surface's
    /// foreground.
    /// </summary>
    private void ApplyRunBrushes()
    {
        var brushes = new Dictionary<TreeRunStyle, IBrush>();
        void Add(TreeRunStyle style, string key)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush)
                brushes[style] = brush;
        }

        Add(TreeRunStyle.Name, "AppAccentBrush");
        Add(TreeRunStyle.String, "AppJsonStringBrush");
        Add(TreeRunStyle.Number, "AppJsonNumberBrush");
        Add(TreeRunStyle.Keyword, "AppJsonBoolBrush");
        Add(TreeRunStyle.Literal, "AppJsonNullBrush");
        Add(TreeRunStyle.Hint, "AppMutedTextBrush");
        Add(TreeRunStyle.Link, "AppMutedTextBrush");
        Add(TreeRunStyle.Comment, "AppMutedTextBrush");
        Surface.RunBrushes = brushes;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Loaded -= OnLoaded;
        DataContextChanged -= OnDataContextChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        Surface.SelectionChanged -= OnSurfaceSelectionChanged;
        Surface.LinkClicked -= OnLinkClicked;
        Surface.ExpandLimitReached -= OnExpandLimitReached;
        Surface.WidestRowWidthChanged -= OnWidestRowWidthChanged;
        Surface.PanRequested -= OnPanRequested;
        Surface.SizeChanged -= OnSurfaceSizeChanged;
        PanScrollBar.ValueChanged -= OnPanValueChanged;
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

        UpdatePanRange();
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
        UpdatePanRange();
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
                RawJumpService.Request(raw.Offset);
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

    // ---- horizontal pan ---------------------------------------------------------------

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePanRange();
        ApplyPendingReveal();
    }

    private void OnWidestRowWidthChanged(object? sender, EventArgs e) => UpdatePanRange();

    private void OnPanValueChanged(object? sender, RangeBaseValueChangedEventArgs e) => Surface.PanOffset = e.NewValue;

    private void OnPanRequested(object? sender, double desiredOffset)
    {
        if (PanScrollBar.IsVisible)
            PanScrollBar.Value = Math.Clamp(desiredOffset, PanScrollBar.Minimum, PanScrollBar.Maximum);
    }

    /// <summary>
    /// Sizes the pan scrollbar from the widest row the surface has laid out - a high-water mark,
    /// so the range never shrinks under the user mid-scroll - against the width the rows have.
    /// </summary>
    private void UpdatePanRange()
    {
        double viewport = Surface.ContentViewportWidth;
        double maximum = Math.Max(0, Surface.WidestRowWidth - viewport);
        if (Surface.Document is null || viewport <= 0 || maximum <= 0)
        {
            PanScrollBar.IsVisible = false;
            PanScrollBar.Value = 0;
            Surface.PanOffset = 0;
            return;
        }

        PanScrollBar.Maximum = maximum;
        PanScrollBar.ViewportSize = viewport;
        PanScrollBar.LargeChange = viewport;
        PanScrollBar.SmallChange = TreeSurface.IndentWidth * 2;
        if (PanScrollBar.Value > maximum)
            PanScrollBar.Value = maximum;
        PanScrollBar.IsVisible = true;
    }
}
