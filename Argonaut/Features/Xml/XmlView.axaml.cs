using System;
using System.ComponentModel;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Xml.Tree;
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
using Avalonia.VisualTree;

namespace Argonaut.Features.Xml;

/// <summary>
/// The XML tree: a <see cref="TreeSurface"/> over the view model's <see cref="XmlViewModel.Tree"/>,
/// a pan scrollbar under it, and the <see cref="TreePathBar"/>. Everything a row does beyond selecting and
/// expanding arrives as a link the surface raises - the jump to the raw view for a truncated
/// value - and is acted on here.
/// </summary>
public partial class XmlView : UserControl
{
    private XmlViewModel? subscribedViewModel;
    private MenuFlyout? nodeMenu;

    private readonly RowScrollBars scrollBars;

    public XmlView()
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

        // Right-click opens the node menu. The surface has already selected the row on the
        // press, so the menu acts on what is now selected.
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
    private void ApplyRunBrushes() => XmlTreePalette.Apply(Surface, this);

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

        if (DataContext is XmlViewModel vm)
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
        if (sender is not XmlViewModel vm)
            return;

        if (e.PropertyName is null or nameof(XmlViewModel.HighlightTerm))
        {
            Surface.HighlightTerm = vm.HighlightTerm;
            Surface.CurrentMatch = vm.CurrentMatchIn(Surface.SelectedRow);
        }
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
        Surface.CurrentMatch = vm.CurrentMatchIn(Surface.SelectedRow);
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
        if (e.Link is ViewInRawLink raw)
            RawJumpService.Request(ByteRange.At((subscribedViewModel?.ScanTarget.Offset ?? 0) + raw.Offset));
    }

    /// <summary>
    /// Right-click offers what can be done with the node under the pointer. The menu's items act
    /// on the selection, as the footer's buttons do, and the press has already selected the row
    /// clicked - a closing row stands for the container it closes.
    /// </summary>
    private void OnSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || Surface.SelectedRow is null)
            return;

        e.Handled = true;
        (nodeMenu ??= BuildNodeMenu()).ShowAt(Surface, showAtPointer: true);
    }

    private MenuFlyout BuildNodeMenu()
    {
        var menu = new MenuFlyout();
        // Copy value first: it is what right-click did on its own, and still the likeliest want.
        AddNodeMenuItem(menu, "Copy value", OnCopyValueClick);
        AddNodeMenuItem(menu, "Copy XPath", OnCopyPathClick);
        AddNodeMenuItem(menu, "Show in text view", OnShowInTextClick);
        return menu;
    }

    private static void AddNodeMenuItem(MenuFlyout menu, string header, EventHandler<RoutedEventArgs> click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        menu.Items.Add(item);
    }

    private async void OnCopyValueClick(object? sender, RoutedEventArgs e) => await PathBar.CopyValueAsync();

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e) => await PathBar.CopyPathAsync();

    private void OnShowInTextClick(object? sender, RoutedEventArgs e) => subscribedViewModel?.ShowSelectionInText();

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyPendingReveal();
}
