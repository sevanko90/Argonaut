using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Reactive;

namespace Argonaut.Features.Raw;

/// <summary>
/// Host for <see cref="RawTextSurface"/>. Everything about how a row looks lives in the surface;
/// what remains here is the chrome around it - the pan scrollbar, the reveal a search hit needs,
/// and the scroll reset a wrap-width change needs.
/// </summary>
public partial class RawView : UserControl
{
    private readonly IDisposable fontResourceSubscription;
    private RawViewModel? subscribedViewModel;
    private FontFamily? contentFontFamily;

    public RawView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Surface.SizeChanged += OnSurfaceSizeChanged;
        Surface.PanRequested += OnPanRequested;
        Surface.WidestRowWidthChanged += OnWidestRowWidthChanged;
        PanScrollBar.ValueChanged += OnPanValueChanged;
        fontResourceSubscription = this.GetResourceObservable("AppContentFontFamily")
            .Subscribe(new AnonymousObserver<object?>(OnContentFontChanged));
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is RawViewModel vm)
            RevealSelectedRow(vm);

        UpdatePanRange();

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

        UpdatePanRange();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RawViewModel vm)
            return;

        if (e.PropertyName is null or nameof(RawViewModel.SelectedRowIndex))
            RevealSelectedRow(vm);

        if (e.PropertyName is null or nameof(RawViewModel.WrapWidth))
        {
            // Row geometry is about to change wholesale, so the old vertical offset means
            // nothing against the new rows - and leaving it in place would have the surface
            // draw a viewport far past the end of a row set that starts near-empty and then
            // grows by millions of rows a second.
            ResetScroll();
            UpdatePanRange();
        }
    }

    private void ResetScroll()
    {
        if (RowsScroller.Offset != default)
            RowsScroller.Offset = default;

        PanScrollBar.Value = 0;
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
        Surface.SizeChanged -= OnSurfaceSizeChanged;
        Surface.PanRequested -= OnPanRequested;
        Surface.WidestRowWidthChanged -= OnWidestRowWidthChanged;
        PanScrollBar.ValueChanged -= OnPanValueChanged;
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

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePanRange();

    /// <summary>
    /// A row wider than anything measured so far came into view. Raised from the surface's layout
    /// pass, so the pan range is only ever resized by rows that have actually been laid out.
    /// </summary>
    private void OnWidestRowWidthChanged(object? sender, EventArgs e) => UpdatePanRange();

    private void OnContentFontChanged(object? value)
    {
        contentFontFamily = value as FontFamily;
        UpdatePanRange();
    }

    private void OnPanValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => Surface.PanOffset = e.NewValue;

    /// <summary>
    /// The caret moved somewhere the current pan does not show. The surface asks rather than
    /// setting its own offset, because the pan scrollbar is the thing that owns that value and
    /// has to stay in step with it.
    /// </summary>
    private void OnPanRequested(object? sender, double desiredOffset)
    {
        if (!PanScrollBar.IsVisible)
            return;

        PanScrollBar.Value = Math.Clamp(desiredOffset, PanScrollBar.Minimum, PanScrollBar.Maximum);
    }

    /// <summary>
    /// Sizes the pan scrollbar from the widest row the surface has actually laid out
    /// (<see cref="RawTextSurface.WidestRowWidth"/>), which is a high-water mark and so never
    /// shrinks under the user mid-scroll.
    ///
    /// It used to be an estimate - wrap-width bytes x the advance of "W" - and that was wrong by
    /// a large factor in the common case, which is what this replaced. Two overestimates
    /// compounded: a row's text has far fewer characters than bytes wherever the content is not
    /// ASCII (multi-byte characters collapse, and an invalid run collapses to one U+FFFD), and
    /// "W" is the widest glyph in a proportional font while real text averages closer to half of
    /// it. At wrap 160 the bar therefore claimed roughly twice the width the text ever occupied,
    /// and panning right ran into empty space.
    /// </summary>
    private void UpdatePanRange()
    {
        double viewWidth = Surface.Bounds.Width;
        if (DataContext is not RawViewModel || viewWidth <= 0)
        {
            HidePanBar();
            return;
        }

        double charWidth = MeasureCharWidth();
        double textViewport = Math.Max(
            0,
            viewWidth - (2 * RawTextSurface.ContentPaddingX) - RawTextSurface.LineNumberColumnWidth - RawTextSurface.WrapGutterWidth);

        double maximum = Math.Max(0, Surface.WidestRowWidth - textViewport);
        if (maximum <= 0 || textViewport <= 0)
        {
            HidePanBar();
            return;
        }

        PanScrollBar.Maximum = maximum;
        PanScrollBar.ViewportSize = textViewport;
        PanScrollBar.LargeChange = textViewport;
        PanScrollBar.SmallChange = charWidth * 4;
        if (PanScrollBar.Value > maximum)
            PanScrollBar.Value = maximum;
        PanScrollBar.IsVisible = true;
    }

    private void HidePanBar()
    {
        PanScrollBar.IsVisible = false;
        PanScrollBar.Value = 0;
        Surface.PanOffset = 0;
    }

    private double MeasureCharWidth()
    {
        var typeface = new Typeface(contentFontFamily ?? FontFamily.Default);
        var layout = new Avalonia.Media.TextFormatting.TextLayout("W", typeface, Surface.FontSize, Brushes.Black);
        return layout.WidthIncludingTrailingWhitespace;
    }
}
