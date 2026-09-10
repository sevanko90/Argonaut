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
        PanScrollBar.ValueChanged += OnPanValueChanged;
        fontResourceSubscription = this.GetResourceObservable("AppContentFontFamily")
            .Subscribe(new AnonymousObserver<object?>(OnContentFontChanged));
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is RawViewModel vm)
            RevealSelectedRow(vm);

        UpdatePanRange();
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
        if (vm.SelectedRowIndex is int row && row >= 0 && row < vm.RowCount)
            Surface.ScrollRowIntoView(row);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Surface.SizeChanged -= OnSurfaceSizeChanged;
        Surface.PanRequested -= OnPanRequested;
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
    /// Sizes the pan scrollbar from a deterministic estimate: wrap-width bytes x one measured
    /// character advance. Row text never has more chars than bytes (see RawRowReader), and "W" is
    /// a wide advance in either content font, so this is an upper bound - at the smaller wrap
    /// widths it collapses to zero and the bar hides entirely. An estimate is deliberate: a range
    /// measured from the rows actually on screen would jump as the user scrolled.
    /// </summary>
    private void UpdatePanRange()
    {
        double viewWidth = Surface.Bounds.Width;
        if (DataContext is not RawViewModel vm || viewWidth <= 0)
        {
            HidePanBar();
            return;
        }

        double charWidth = MeasureCharWidth();
        double textViewport = Math.Max(
            0,
            viewWidth - (2 * RawTextSurface.ContentPaddingX) - RawTextSurface.LineNumberColumnWidth - RawTextSurface.WrapGutterWidth);

        double maximum = Math.Max(0, vm.WrapWidth * charWidth - textViewport);
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
