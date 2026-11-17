using System;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Argonaut.Features.Hex;

/// <summary>A document as a hex dump - see <see cref="HexViewModel"/> and <see cref="HexSurface"/>.</summary>
public partial class HexView : UserControl
{
    private readonly RowScrollBars scrollBars;

    public HexView()
    {
        InitializeComponent();
        scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        DetachedFromVisualTree += (_, _) => scrollBars.Dispose();

        // The dump is the document, so it takes focus when the document is shown: the caret
        // shows solid and the keys reach it, rather than focus landing on the window's chrome.
        // Nothing else has been focused yet at load time, so this takes focus from nobody.
        Loaded += (_, _) => Surface.Focus();

        // Ctrl/Cmd+L opens the go-to entry from anywhere in the view, as it opens a tree view's
        // path bar. Tunnelling, so the surface's own keys never see it, and the window's new
        // request (which waits for keys nothing claimed) does not either.
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.L && e.KeyModifiers == command)
        {
            OpenGoToEntry();
            e.Handled = true;
        }
    }

    private void OnWidenValues(object? sender, RoutedEventArgs e) => (DataContext as HexViewModel)?.Values.Widen();

    private void OnNarrowValues(object? sender, RoutedEventArgs e) => (DataContext as HexViewModel)?.Values.Narrow();

    private void OnUseSystemOrder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HexViewModel vm)
            vm.Values.UseSystemOrder();
    }

    private void OnValueRowEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: HexValueRow row } && DataContext is HexViewModel vm)
            vm.Values.Highlight(row);
    }

    private void OnValueRowExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is HexViewModel vm)
            vm.Values.Highlight(null);
    }

    /// <summary>Copies the value clicked, as it reads - a reading there are too few bytes for has
    /// nothing to copy.</summary>
    private async void OnValueTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock { Text: { Length: > 0 } value } || value == "-" || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(value);
        ToastService.Show($"Copied {value}");
    }

    private void OnCaretPositionClick(object? sender, RoutedEventArgs e) => OpenGoToEntry();

    /// <summary>Swaps the caret's position for an entry holding its offset in hex, all selected,
    /// so typing replaces it and an arrow key edits it.</summary>
    internal void OpenGoToEntry()
    {
        if (DataContext is not HexViewModel vm)
            return;

        GoToEntry.Text = vm.GoToStartText;
        CaretPositionButton.IsVisible = false;
        GoToEntry.IsVisible = true;
        GoToEntry.Focus();
        GoToEntry.SelectAll();
    }

    private void CloseGoToEntry()
    {
        GoToEntry.IsVisible = false;
        CaretPositionButton.IsVisible = true;
    }

    /// <summary>Goes to what was typed. An entry that is no offset, or one past the end, says why
    /// and stays open to be put right.</summary>
    internal void GoToEnteredOffset()
    {
        if (DataContext is not HexViewModel vm)
            return;

        if (vm.GoToOffset(GoToEntry.Text) is { } problem)
        {
            ToastService.Show(problem);
            GoToEntry.SelectAll();
            return;
        }

        CloseGoToEntry();
        Surface.Focus();
    }

    private void OnGoToEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoToEnteredOffset();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseGoToEntry();
            Surface.Focus();
            e.Handled = true;
        }
    }

    private void OnGoToEntryLostFocus(object? sender, RoutedEventArgs e) => CloseGoToEntry();

    private HexViewModel? shown;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.shown is not null)
            this.shown.Grew -= OnDocumentGrew;
        this.shown = DataContext as HexViewModel;
        if (this.shown is not null)
            this.shown.Grew += OnDocumentGrew;

        Surface.Document = this.shown;
        scrollBars.Refresh();
    }

    /// <summary>More of a streamed document arrived: draw its new rows and let the scrollbar
    /// reach them.</summary>
    private void OnDocumentGrew(object? sender, EventArgs e)
    {
        Surface.ShowGrowth();
        scrollBars.Refresh();
    }
}
