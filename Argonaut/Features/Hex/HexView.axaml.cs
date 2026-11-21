using System;
using System.ComponentModel;
using Argonaut.Features.Hex.Structs;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Argonaut.Features.Hex;

/// <summary>A document as a hex dump - see <see cref="HexViewModel"/> and <see cref="HexSurface"/>.</summary>
public partial class HexView : UserControl
{
    private readonly RowScrollBars scrollBars;
    private readonly RowScrollBars structScrollBars;

    public HexView()
    {
        InitializeComponent();
        scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        structScrollBars = new RowScrollBars(StructSurface, StructVerticalScrollBar, StructPanScrollBar);
        DetachedFromVisualTree += (_, _) =>
        {
            scrollBars.Dispose();
            structScrollBars.Dispose();
        };
        ActualThemeVariantChanged += (_, _) => StructTreePalette.Apply(StructSurface, this);

        SizeChanged += (_, _) => FitSidePanel();
        Surface.WidestRowWidthChanged += (_, _) => FitSidePanel();
        VerticalScrollBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == BoundsProperty)
                FitSidePanel();
        };
        FitSidePanel();

        // The dump is the document, so it takes focus when the document is shown: the caret
        // shows solid and the keys reach it, rather than focus landing on the window's chrome.
        // Nothing else has been focused yet at load time, so this takes focus from nobody.
        Loaded += (_, _) => Surface.Focus();

        // Ctrl/Cmd+L opens the go-to entry from anywhere in the view, as it opens a tree view's
        // path bar. Tunnelling, so the surface's own keys never see it, and the window's new
        // request (which waits for keys nothing claimed) does not either.
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The side panel's width when the window has no room to spare.</summary>
    internal const double SidePanelMinWidth = 340;

    /// <summary>Anchors the side panel to the dump: when the window is wider than both need, the
    /// dump stays at its drawn width and the panel takes the rest; when it is narrower, the panel
    /// keeps its minimum and the dump gives up the room, panning as before.</summary>
    private void FitSidePanel()
    {
        double bar = VerticalScrollBar.IsVisible ? VerticalScrollBar.Bounds.Width : 0;
        SidePanel.Width = Math.Max(SidePanelMinWidth, Bounds.Width - Surface.ContentWidth - bar);
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
        {
            this.shown.Grew -= OnDocumentGrew;
            this.shown.Structs.PropertyChanged -= OnStructsChanged;
            this.shown.Structs.Moved -= OnStructMoved;
        }

        this.shown = DataContext as HexViewModel;
        if (this.shown is not null)
        {
            this.shown.Grew += OnDocumentGrew;
            this.shown.Structs.PropertyChanged += OnStructsChanged;
            this.shown.Structs.Moved += OnStructMoved;
        }

        Surface.Document = this.shown;
        scrollBars.Refresh();
        ShowStructTree();
    }

    /// <summary>The selector's list of the structs declared, built as it opens; picking one
    /// shows it.</summary>
    private void OnStructTypesOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || this.shown?.Structs is not { } structs)
            return;

        menu.Items.Clear();
        foreach (string name in structs.Types)
        {
            var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = name == structs.SelectedType, FontFamily = StructSurface.FontFamily };
            item.Click += (_, _) => structs.SelectedType = name;
            menu.Items.Add(item);
        }
    }

    /// <summary>The target chip's choices, built as it opens.</summary>
    private void OnStructTargetsOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || this.shown?.Structs is not { } structs)
            return;

        menu.Items.Clear();
        foreach (var target in structs.Targets)
        {
            var item = new MenuItem { Header = target.Name, ToggleType = MenuItemToggleType.Radio, IsChecked = target == structs.Target };
            item.Click += (_, _) => structs.Target = target;
            menu.Items.Add(item);
        }
    }

    private void OnInsertCSharpSample(object? sender, RoutedEventArgs e) => this.shown?.Structs.InsertSample(StructLanguage.CSharp);

    private void OnInsertCSample(object? sender, RoutedEventArgs e) => this.shown?.Structs.InsertSample(StructLanguage.C);

    /// <summary>A paste that replaces the whole definition - into an empty editor, or over all of
    /// it - is a new definition: its language is told from it again. A paste into a definition
    /// keeps the language chosen.</summary>
    private void OnDefinitionPasting(object? sender, RoutedEventArgs e)
    {
        string text = DefinitionEditor.Text ?? string.Empty;
        int selected = Math.Abs(DefinitionEditor.SelectionEnd - DefinitionEditor.SelectionStart);
        if (text.Length == 0 || selected >= text.Length)
            this.shown?.Structs.DefinitionPasted();
    }

    private void OnStructsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StructDecoderViewModel.Tree))
            ShowStructTree();
    }

    /// <summary>A new tree - another struct, target or array length - starts at the top.</summary>
    private void ShowStructTree()
    {
        var tree = this.shown?.Structs.Tree;
        if (tree is not null)
            tree.OffsetGutter.CharacterWidth = MonoCharacterWidth();
        StructSurface.Document = tree;
        StructTreePalette.Apply(StructSurface, this);
        structScrollBars.ResetPan();
        structScrollBars.Refresh();
    }

    /// <summary>The same tree reading from elsewhere, or in another byte order: its rows are the
    /// same, so the surface keeps its place and lays the rows out again.</summary>
    private void OnStructMoved(object? sender, EventArgs e)
    {
        StructSurface.InvalidateRows();
        structScrollBars.Refresh();
    }

    private double MonoCharacterWidth()
    {
        using var sample = new TextLayout(new string('0', 16), new Typeface(StructSurface.FontFamily), StructSurface.FontSize, Brushes.Black);
        return sample.WidthIncludingTrailingWhitespace / 16;
    }

    /// <summary>More of a streamed document arrived: draw its new rows and let the scrollbar
    /// reach them.</summary>
    private void OnDocumentGrew(object? sender, EventArgs e)
    {
        Surface.ShowGrowth();
        scrollBars.Refresh();
    }
}
