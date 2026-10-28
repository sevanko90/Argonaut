using System;
using System.Threading.Tasks;
using Argonaut.Ui.Notifications;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Argonaut.Ui.Tree;

/// <summary>
/// The bar at the foot of a tree view: where the selection is, as clickable path segments, and
/// where to go. Its data context is the <see cref="TreeDocumentViewModel"/> the tree shows. The
/// format tells it only how to speak - the icon, what the entry and the empty bar say, the
/// tooltips - because the path syntax itself (JSONPath, XPath) is the view model's.
///
/// Clicking its empty space, the go-to button or Ctrl/Cmd+L anywhere in the enclosing view turns
/// the segments into an entry prefilled with the selection's path.
/// </summary>
public partial class TreePathBar : UserControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<TreePathBar, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> EntryPlaceholderProperty =
        AvaloniaProperty.Register<TreePathBar, string?>(nameof(EntryPlaceholder));

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<TreePathBar, string?>(nameof(EmptyText));

    public static readonly StyledProperty<string?> GoToTooltipProperty =
        AvaloniaProperty.Register<TreePathBar, string?>(nameof(GoToTooltip));

    public static readonly StyledProperty<string?> CopyPathTooltipProperty =
        AvaloniaProperty.Register<TreePathBar, string?>(nameof(CopyPathTooltip));

    public static readonly StyledProperty<string?> CopiedPathMessageProperty =
        AvaloniaProperty.Register<TreePathBar, string?>(nameof(CopiedPathMessage));

    public static readonly StyledProperty<string> RootPathProperty =
        AvaloniaProperty.Register<TreePathBar, string>(nameof(RootPath), string.Empty);

    public static readonly StyledProperty<Control?> FocusTargetProperty =
        AvaloniaProperty.Register<TreePathBar, Control?>(nameof(FocusTarget));

    private UserControl? shortcutScope;

    public TreePathBar()
    {
        InitializeComponent();
    }

    /// <summary>The icon at the bar's left edge.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>What the empty path entry suggests, e.g. <c>$.path.to[0]</c>.</summary>
    public string? EntryPlaceholder
    {
        get => GetValue(EntryPlaceholderProperty);
        set => SetValue(EntryPlaceholderProperty, value);
    }

    /// <summary>What the bar says with nothing selected.</summary>
    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>Tooltip of the go-to button, naming the path syntax and the shortcut.</summary>
    public string? GoToTooltip
    {
        get => GetValue(GoToTooltipProperty);
        set => SetValue(GoToTooltipProperty, value);
    }

    /// <summary>Tooltip of the copy-path button.</summary>
    public string? CopyPathTooltip
    {
        get => GetValue(CopyPathTooltipProperty);
        set => SetValue(CopyPathTooltipProperty, value);
    }

    /// <summary>The toast shown once the path is on the clipboard.</summary>
    public string? CopiedPathMessage
    {
        get => GetValue(CopiedPathMessageProperty);
        set => SetValue(CopiedPathMessageProperty, value);
    }

    /// <summary>The entry's text when nothing is selected: the path of the document's root.</summary>
    public string RootPath
    {
        get => GetValue(RootPathProperty);
        set => SetValue(RootPathProperty, value);
    }

    /// <summary>The control that takes focus back when the entry closes - the tree's surface.</summary>
    public Control? FocusTarget
    {
        get => GetValue(FocusTargetProperty);
        set => SetValue(FocusTargetProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Ctrl/Cmd+L opens the entry from anywhere in the enclosing view, the browser's "go to
        // address" key. Tunnelling, so the tree's own key handling never sees it.
        shortcutScope = this.FindAncestorOfType<UserControl>();
        shortcutScope?.AddHandler(KeyDownEvent, OnScopeKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        shortcutScope?.RemoveHandler(KeyDownEvent, OnScopeKeyDown);
        shortcutScope = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScopeKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.L && e.KeyModifiers == command)
        {
            OpenEntry();
            e.Handled = true;
        }
    }

    private void OnPathSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TreePathSegment segment } && DataContext is TreeDocumentViewModel vm)
            vm.Reveal(segment.Target);
    }

    private void OnPathBarPressed(object? sender, PointerPressedEventArgs e)
    {
        // A press on a segment is the segment's; only the bar's own space opens the entry.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        OpenEntry();
        e.Handled = true;
    }

    private void OnGoToPathClick(object? sender, RoutedEventArgs e)
    {
        if (PathEntry.IsVisible)
            _ = GoToEnteredPathAsync();
        else
            OpenEntry();
    }

    /// <summary>Swaps the breadcrumb for an entry holding the selection's path, all selected, so
    /// typing replaces it and an arrow key edits it.</summary>
    public void OpenEntry()
    {
        PathEntry.Text = (DataContext as TreeDocumentViewModel)?.SelectedPath ?? RootPath;
        PathDisplay.IsVisible = false;
        PathEntry.IsVisible = true;
        PathEntry.Focus();
        PathEntry.SelectAll();
    }

    private void CloseEntry()
    {
        PathEntry.IsVisible = false;
        PathDisplay.IsVisible = true;
    }

    private async Task GoToEnteredPathAsync()
    {
        string path = PathEntry.Text?.Trim() ?? string.Empty;
        CloseEntry();
        FocusTarget?.Focus();
        if (path.Length > 0 && DataContext is TreeDocumentViewModel vm)
            await vm.NavigateToPathAsync(path);
    }

    private void OnPathEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = GoToEnteredPathAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseEntry();
            FocusTarget?.Focus();
            e.Handled = true;
        }
    }

    private void OnPathEntryLostFocus(object? sender, RoutedEventArgs e) => CloseEntry();

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e) => await CopyPathAsync();

    private async void OnCopyValueClick(object? sender, RoutedEventArgs e) => await CopyValueAsync();

    /// <summary>Puts the selected row's path on the clipboard.</summary>
    public async Task CopyPathAsync()
    {
        if (DataContext is not TreeDocumentViewModel { SelectedPath: { } path })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(path);

        ToastService.Show(CopiedPathMessage ?? "Path copied to clipboard");
    }

    /// <summary>Puts the selected row's value on the clipboard.</summary>
    public async Task CopyValueAsync()
    {
        if (DataContext is not TreeDocumentViewModel { SelectedValueText: { } value })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(value);
        ToastService.Show("Value copied to clipboard");
    }
}
