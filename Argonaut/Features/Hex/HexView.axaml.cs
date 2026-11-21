using System;
using System.Collections.Generic;
using System.ComponentModel;
using Argonaut.Features.Hex.Structs;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        StructSurface.LinkClicked += OnStructLinkClicked;
        StructSurface.HoveredRowChanged += OnStructRowHovered;
        StructSurface.SelectionChanged += OnStructRowSelected;
        StructSurface.ContextMenu = BuildStructMenu();
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
            this.shown.Structs.CaretFieldChanged -= OnStructCaretFieldChanged;
        }

        this.shown = DataContext as HexViewModel;
        if (this.shown is not null)
        {
            this.shown.Grew += OnDocumentGrew;
            this.shown.Structs.PropertyChanged += OnStructsChanged;
            this.shown.Structs.Moved += OnStructMoved;
            this.shown.Structs.CaretFieldChanged += OnStructCaretFieldChanged;
        }

        Surface.Document = this.shown;
        scrollBars.Refresh();
        ShowStructTree();
    }

    /// <summary>Shows <paramref name="items"/> as a menu under <paramref name="anchor"/>. A menu
    /// is built when its button is clicked rather than as it opens: by the time a flyout raises
    /// Opening it has already laid out what it holds, so items added then never show.</summary>
    private static void ShowMenu(Control anchor, PlacementMode placement, IEnumerable<Control> items)
    {
        var menu = new MenuFlyout { Placement = placement };
        foreach (var item in items)
            menu.Items.Add(item);
        menu.ShowAt(anchor);
    }

    /// <summary>The selector's list of the structs declared; picking one shows it.</summary>
    private void OnStructTypesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor && this.shown?.Structs is { } structs)
            ShowMenu(anchor, PlacementMode.BottomEdgeAlignedLeft, StructTypeItems(structs));
    }

    internal IEnumerable<Control> StructTypeItems(StructDecoderViewModel structs)
    {
        foreach (string name in structs.Types)
        {
            var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = name == structs.SelectedType, FontFamily = StructSurface.FontFamily };
            item.Click += (_, _) => structs.SelectedType = name;
            yield return item;
        }
    }

    /// <summary>The target chip's choices.</summary>
    private void OnStructTargetsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || this.shown?.Structs is not { } structs)
            return;

        var items = new List<Control>();
        foreach (var target in structs.Targets)
        {
            var item = new MenuItem { Header = target.Name, ToggleType = MenuItemToggleType.Radio, IsChecked = target == structs.Target };
            item.Click += (_, _) => structs.Target = target;
            items.Add(item);
        }

        ShowMenu(anchor, PlacementMode.BottomEdgeAlignedRight, items);
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

    /// <summary>The struct tree's menu: the row pressed on, or the whole struct, as JSON.</summary>
    private ContextMenu BuildStructMenu()
    {
        var row = new MenuItem { Header = "Copy as JSON" };
        row.Click += (_, _) =>
        {
            if (StructSurface.SelectedRow?.Detail is StructNode node)
                _ = CopyStructJsonAsync(node, node.DisplayLabel);
        };
        var all = new MenuItem { Header = "Copy struct as JSON" };
        all.Click += (_, _) => OnCopyStructAsJson(null, new RoutedEventArgs());
        return new ContextMenu { Items = { row, all } };
    }

    /// <summary>The selector's copy button: the whole mapping - one struct, or the array.</summary>
    private void OnCopyStructAsJson(object? sender, RoutedEventArgs e)
    {
        if (this.shown?.Structs is { Tree: { } tree } structs)
            _ = CopyStructJsonAsync(tree.Nodes.Root, structs.StructTitle);
    }

    private async System.Threading.Tasks.Task CopyStructJsonAsync(StructNode node, string what)
    {
        if (this.shown?.Structs.Tree is not { } tree || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        var result = StructJsonWriter.Write(tree, node);
        await clipboard.SetTextAsync(result.Json);
        ToastService.Show(result.Truncated
            ? $"Copied the first {StructJsonWriter.MaxValues:N0} values of {what} as JSON"
            : $"Copied {what} as JSON");
    }

    /// <summary>The Open menu, read afresh each time since the folder may change outside the app.</summary>
    private void OnOpenDefinitionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor && this.shown?.Structs is { } structs)
            ShowMenu(anchor, PlacementMode.BottomEdgeAlignedRight, DefinitionItems(structs));
    }

    /// <summary>The saved definitions, the recent ones, deleting a saved one, and the folder.</summary>
    internal static List<Control> DefinitionItems(StructDecoderViewModel structs)
    {
        structs.RefreshDefinitions();
        var items = new List<Control>();
        MenuItem Section(string title) => new() { Header = title, IsEnabled = false, FontSize = 11 };
        static string Name(StructDefinitionEntry entry) => $"{entry.Name} · {(entry.Language == StructLanguage.CSharp ? "C#" : "C")}";

        if (structs.Saved.Count > 0)
        {
            items.Add(Section("Saved"));
            foreach (var entry in structs.Saved)
            {
                var item = new MenuItem { Header = Name(entry) };
                item.Click += (_, _) =>
                {
                    if (!structs.Open(entry))
                        ToastService.Show($"Could not read {System.IO.Path.GetFileName(entry.FilePath)}");
                };
                items.Add(item);
            }
        }

        if (structs.Recent.Count > 0)
        {
            if (items.Count > 0)
                items.Add(new Separator());
            items.Add(Section("Recent"));
            foreach (var recent in structs.Recent)
            {
                var item = new MenuItem { Header = recent.Label };
                item.Click += (_, _) => structs.Open(recent);
                items.Add(item);
            }
        }

        if (items.Count == 0)
            items.Add(Section("Nothing saved or recent yet"));

        items.Add(new Separator());
        if (structs.Saved.Count > 0)
        {
            // Two levels deep, so a delete is never one slip of the pointer away.
            var delete = new MenuItem { Header = "Delete saved" };
            foreach (var entry in structs.Saved)
            {
                var item = new MenuItem { Header = Name(entry) };
                item.Click += (_, _) =>
                {
                    if (structs.Delete(entry) is { } problem)
                        ToastService.Show(problem);
                    else
                        ToastService.Show($"Deleted {System.IO.Path.GetFileName(entry.FilePath)}");
                };
                delete.Items.Add(item);
            }

            items.Add(delete);
        }

        var folder = new MenuItem { Header = "Open definitions folder" };
        folder.Click += (_, _) => structs.RevealDefinitionsFolder();
        items.Add(folder);
        return items;
    }

    /// <summary>Save opens on the struct's name, all selected, so typing replaces it.</summary>
    private void OnSaveDefinitionOpening(object? sender, EventArgs e)
    {
        SaveDefinitionName.Text = this.shown?.Structs.SuggestedName ?? string.Empty;
        SaveDefinitionProblem.IsVisible = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SaveDefinitionName.Focus();
            SaveDefinitionName.SelectAll();
        });
    }

    private void OnSaveDefinitionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        SaveDefinition();
        e.Handled = true;
    }

    private void OnSaveDefinitionClick(object? sender, RoutedEventArgs e) => SaveDefinition();

    private void SaveDefinition()
    {
        if (this.shown?.Structs is not { } structs)
            return;

        string name = SaveDefinitionName.Text ?? string.Empty;
        if (structs.Save(name) is { } problem)
        {
            SaveDefinitionProblem.Text = problem;
            SaveDefinitionProblem.IsVisible = true;
            return;
        }

        SaveDefinitionButton.Flyout?.Hide();
        ToastService.Show($"Saved {name.Trim()}");
    }

    // Set while the tree is told to select the caret's field, so that selection is not taken as
    // the user's and sent back to the dump.
    private bool selectingCaretField;

    /// <summary>The row under the pointer: its field is marked in the dump.</summary>
    private void OnStructRowHovered(object? sender, EventArgs e)
        => this.shown?.Structs.Hover(StructSurface.HoveredRow?.Detail is StructNode node ? node : null);

    /// <summary>A row selected in the tree selects its bytes in the dump.</summary>
    private void OnStructRowSelected(object? sender, EventArgs e)
    {
        if (!this.selectingCaretField && this.shown is { } hex && StructSurface.SelectedRow?.Detail is StructNode node)
            hex.SelectStructField(node);
    }

    /// <summary>Pinned, the caret moved to another field: the tree selects it, opening what holds
    /// it.</summary>
    private void OnStructCaretFieldChanged(object? sender, EventArgs e)
    {
        if (this.shown?.Structs.CaretField is not { } node)
            return;

        this.selectingCaretField = true;
        try
        {
            StructSurface.Reveal(node.Position, expandAncestors: true);
        }
        finally
        {
            this.selectingCaretField = false;
        }
    }

    /// <summary>A struct row's "as text" / "as items" chip: the array reads the other way.</summary>
    private void OnStructLinkClicked(object? sender, Argonaut.Ui.Tree.TreeLinkClickedEventArgs e)
    {
        if (e.Link is not StructTextSwitch textSwitch || this.shown?.Structs.Tree is not { } tree)
            return;

        tree.ToggleText(textSwitch.Node);
        StructSurface.InvalidateRows();
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
