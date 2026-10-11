using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using Avalonia.Threading;
using Argonaut.Engine.Hints;
using Argonaut.Features.Raw.Highlighting;
using Argonaut.Features.Raw.Hints;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Raw;

/// <summary>
/// Host for <see cref="RawTextSurface"/>. Everything about how a row looks lives in the surface,
/// and its scrollbars are driven by <see cref="RowScrollBars"/>; what remains here is the chrome
/// around it - the edit overview strip, the reveal a search hit needs, and the scroll reset a
/// wrap-width change needs.
/// </summary>
public partial class RawView : UserControl
{
    private readonly IDisposable fontResourceSubscription;
    private readonly RowScrollBars scrollBars;
    private RawViewModel? subscribedViewModel;

    /// <summary>The marked value whose chip is showing, and the grace the pointer has to cross from
    /// the word onto the chip before it goes.</summary>
    private RawHintHit? markChipFor;
    private readonly DispatcherTimer markChipClose = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public RawView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        ActualThemeVariantChanged += OnThemeChanged;
        scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        EditOverview.EditChosen += OnEditChosen;
        Surface.HintClicked += OnHintClicked;
        Surface.MarkHovered += OnMarkHovered;
        MarkChip.Click += OnMarkChipClick;
        MarkChipPopup.PointerEntered += (_, _) => markChipClose.Stop();
        MarkChipPopup.PointerExited += (_, _) => markChipClose.Start();
        markChipClose.Tick += (_, _) => CloseMarkChip();
        fontResourceSubscription = this.GetResourceObservable("AppContentFontFamily")
            .Subscribe(new AnonymousObserver<object?>(OnContentFontChanged));
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTextBrushes();

        if (DataContext is RawViewModel vm)
            RevealSelectedRow(vm);

        scrollBars.Refresh();

        // The surface is the document, so it takes focus when the document is shown. Without
        // this the caret is invisible (it is hidden while unfocused) and arrow keys never reach
        // the editor - unhandled, they fall through to directional navigation and walk focus off
        // to the find bar. Safe to do here: nothing else has been focused yet at load time, so
        // this cannot steal focus from the find box, which is focused later and by the user.
        Surface.Focus();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTextBrushes();

    /// <summary>
    /// What a hint does when clicked: a date offers the ways to read the document's dates, an
    /// address opens, and a value hiding a document opens the card showing it.
    /// </summary>
    private void OnHintClicked(object? sender, RawHintHit hit)
    {
        if (hit.Value.Hint is not { } hint || subscribedViewModel is not { } vm)
            return;

        if (hint.Expands)
            _ = ShowHintCardAsync(vm, hit);
        else if (hint.Kind == ValueHintKind.Date)
            DateSchemeMenu(vm).ShowAt(Surface, showAtPointer: true);
        else if (hint.Address is { } address && UrlHintProvider.IsSafe(address))
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(address);
    }

    private async Task ShowHintCardAsync(RawViewModel vm, RawHintHit hit)
    {
        var shown = await vm.PreviewHintAsync(hit.Value);
        if (shown is null)
        {
            ToastService.Show("That value doesn't decode after all.");
            return;
        }

        HintCard.Show(shown, hit.Bounds);
    }

    /// <summary>The ways a number can read as a date, for the whole document: a raw view has no
    /// per-value choice, since an edit moves every value after it.</summary>
    private static MenuFlyout DateSchemeMenu(RawViewModel vm)
    {
        var menu = new MenuFlyout();
        void Add(string header, DateDecodingScheme scheme)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = vm.HintSettings.FileDefaultScheme == scheme ? new TextBlock { Text = "✓" } : null,
            };
            item.Click += (_, _) => vm.HintSettings.SetUserDefault(scheme);
            menu.Items.Add(item);
        }

        Add("Off", DateDecodingScheme.Off);
        Add("JS milliseconds", DateDecodingScheme.JsMilliseconds);
        Add("JS seconds", DateDecodingScheme.JsSeconds);
        Add("Keepa minutes", DateDecodingScheme.KeepaMinutes);
        return menu;
    }

    /// <summary>The text palette, from the theme - again whenever the theme changes.</summary>
    private void ApplyTextBrushes()
    {
        RawTextPalette.Apply(Surface, this);
        MarkChip.Icons = Surface.HintLayer.Icons;
        MarkChip.InvalidateVisual();
    }

    /// <summary>
    /// Shows the chip of the value under the pointer just below it, or - when the pointer has left
    /// it - closes the chip after a moment, long enough to reach it.
    /// </summary>
    private void OnMarkHovered(object? sender, RawHintHit? hit)
    {
        if (hit?.Value.Hint is not { } hint)
        {
            markChipClose.Start();
            return;
        }

        markChipClose.Stop();
        markChipFor = hit;
        MarkChip.Hint = hint;
        MarkChipPopup.PlacementRect = hit.Bounds;
        MarkChipPopup.IsOpen = false;
        MarkChipPopup.IsOpen = true;
    }

    private void OnMarkChipClick(object? sender, EventArgs e)
    {
        var hit = markChipFor;
        CloseMarkChip();
        if (hit is not null)
            OnHintClicked(Surface, hit);
    }

    private void CloseMarkChip()
    {
        markChipClose.Stop();
        MarkChipPopup.IsOpen = false;
        markChipFor = null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel.ToolbarChoiceMade -= OnToolbarChoiceMade;
            subscribedViewModel = null;
        }

        if (DataContext is RawViewModel vm)
        {
            subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.ToolbarChoiceMade += OnToolbarChoiceMade;
        }

        Surface.Lexer = (DataContext as RawViewModel)?.Lexer;
        scrollBars.Refresh();
        UpdateEditOverview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RawViewModel vm)
            return;

        if (e.PropertyName is null or nameof(RawViewModel.SelectedRowIndex))
            RevealSelectedRow(vm);

        if (e.PropertyName is null or nameof(RawViewModel.Lexer))
            Surface.Lexer = vm.Lexer;

        // The toggle that turns editing on lives in the header toolbar, so the click that
        // enabled it left focus there. Typing has to work without a second click into the text.
        if ((e.PropertyName is null or nameof(RawViewModel.IsEditing)) && vm.IsEditing)
            Surface.Focus();

        if (e.PropertyName is null or nameof(RawViewModel.IsEditing))
            UpdateEditOverview();
        else if (e.PropertyName is nameof(RawViewModel.EditGeneration))
            EditOverview.Refresh();

        if (e.PropertyName is null or nameof(RawViewModel.WrapWidth) or nameof(RawViewModel.IndexGeneration))
        {
            // Row geometry is about to change wholesale (a re-wrap, or the fresh scan a save
            // reopens the document with), so the old vertical offset means nothing against the
            // new rows - and leaving it in place would have the surface draw a viewport far past
            // the end of a row set that starts near-empty and then grows by millions of rows a
            // second. A save's reopen puts the caret back with a reveal once the scan reaches it;
            // a re-wrap hands over the place the user was at, which the surface lands once the
            // new rows reach it.
            Surface.ResetScroll();
            if (vm.TakePlaceAcrossRewrap() is { } place)
                Surface.KeepPlace(place.Offset, place.ScreenRow);
            scrollBars.ResetPan();
            scrollBars.Refresh();
        }
    }

    /// <summary>
    /// Scrolls the row a search reveal or a jump-to-offset asked for into view. There is no
    /// selection to mirror any more - the surface draws a caret and a byte-range selection, and a
    /// reveal is purely a scroll.
    /// </summary>
    private void RevealSelectedRow(RawViewModel vm)
    {
        if (vm.SelectedRowIndex is int row && row >= 0)
            Surface.RevealRow(row);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        scrollBars.Dispose();
        ActualThemeVariantChanged -= OnThemeChanged;
        EditOverview.EditChosen -= OnEditChosen;
        DataContextChanged -= OnDataContextChanged;
        fontResourceSubscription.Dispose();

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel.ToolbarChoiceMade -= OnToolbarChoiceMade;
            subscribedViewModel = null;
        }

        // Disposed synchronously here as an idempotent safety net for teardown the shell does not
        // drive (e.g. window close); the shell disposes the outgoing document before the swap.
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    /// <summary>
    /// Points the edit overview at the editor, once there is one - it stays shown after edit
    /// mode is turned off while the edits it marks are still unsaved, since that is when finding
    /// them again matters.
    /// </summary>
    private void UpdateEditOverview()
    {
        var editor = (DataContext as RawViewModel)?.Editor;
        EditOverview.IsVisible = editor is not null;
        EditOverview.Show(editor?.Document, editor?.RowIndex);
    }

    /// <summary>
    /// A wrap width or colours were picked from the toolbar: focus goes back to the text. Deferred because the
    /// choice arrives inside the combo's selection commit, and moving focus away from a control
    /// mid-commit is the kind of side effect CLAUDE.md's selection-bound setter rule hands to
    /// <see cref="UiDeferral.AfterCurrentInput"/>.
    /// </summary>
    private void OnToolbarChoiceMade(object? sender, EventArgs e) => UiDeferral.AfterCurrentInput(() => Surface.Focus());

    /// <summary>A mark on the overview was clicked: put the caret on the edit it stands for.</summary>
    private void OnEditChosen(object? sender, long offset)
    {
        if (DataContext is not RawViewModel { RowIndex: { } rows } vm)
            return;

        int row = rows.RowForOffset(offset) ?? Math.Max(rows.RowCount - 1, 0);
        vm.RevealOffset(offset, row);
        Surface.Focus();
    }

    /// <summary>The content font changed: the pan step, and the widths already measured with
    /// the old font, are stale.</summary>
    private void OnContentFontChanged(object? value) => scrollBars.Refresh();
}
