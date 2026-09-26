using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using Argonaut.Ui.Rows;

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

    public RawView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        EditOverview.EditChosen += OnEditChosen;
        fontResourceSubscription = this.GetResourceObservable("AppContentFontFamily")
            .Subscribe(new AnonymousObserver<object?>(OnContentFontChanged));
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        if (DataContext is RawViewModel vm)
        {
            subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        scrollBars.Refresh();
        UpdateEditOverview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RawViewModel vm)
            return;

        if (e.PropertyName is null or nameof(RawViewModel.SelectedRowIndex))
            RevealSelectedRow(vm);

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
            // second. A save's reopen puts the caret back with a reveal once the scan reaches it.
            Surface.ResetScroll();
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
        EditOverview.EditChosen -= OnEditChosen;
        DataContextChanged -= OnDataContextChanged;
        fontResourceSubscription.Dispose();

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
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
