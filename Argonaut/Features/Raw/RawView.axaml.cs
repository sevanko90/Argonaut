using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Reactive;

namespace Argonaut.Features.Raw;

public partial class RawView : UserControl
{
    // Must mirror the row template's Grid ColumnDefinitions and the ListBox Padding - the pan
    // range is estimated, not measured from realized rows (a deterministic scrollbar beats an
    // exact one whose range jumps as rows realize).
    private const double LineNumberColumnWidth = 72;
    private const double WrapGutterColumnWidth = 18;
    private const double ListBoxHorizontalPadding = 16;

    private readonly TranslateTransform panTransform;
    private readonly IDisposable fontResourceSubscription;
    private bool suppressSelectionEvents;
    private RawViewModel? subscribedViewModel;
    private FontFamily? contentFontFamily;

    public RawView()
    {
        InitializeComponent();
        panTransform = (TranslateTransform)Resources["PanTransform"]!;

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        RowsListBox.SelectionChanged += OnSelectionChanged;
        RowsListBox.SizeChanged += OnListBoxSizeChanged;
        RowsListBox.PropertyChanged += OnListBoxPropertyChanged;
        PanScrollBar.ValueChanged += OnPanValueChanged;
        fontResourceSubscription = this.GetResourceObservable("AppContentFontFamily")
            .Subscribe(new AnonymousObserver<object?>(OnContentFontChanged));
    }

    /// <summary>
    /// A wrap-width change swaps the ItemsSource wholesale (see RawViewModel.SetWrapWidth).
    /// The old vertical offset is meaningless against the new row geometry - and leaving it
    /// in place makes the virtualizer reconcile an offset potentially tens of millions of
    /// pixels deep against a fresh source that starts near-empty and then grows by millions
    /// of rows a second - so snap back to the top on every source swap.
    /// </summary>
    private void OnListBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty && RowsListBox.Scroll is { } scroll)
            scroll.Offset = default;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is RawViewModel vm)
            SyncVisualSelection(vm);

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

    /// <summary>
    /// Mirrors programmatic selection (a search reveal calling SelectRow) into the ListBox -
    /// user clicks already go the other way via OnSelectionChanged, and SyncVisualSelection
    /// suppresses the echo. A wrap-width change re-ranges the pan scrollbar.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RawViewModel vm)
            return;

        if (e.PropertyName is null or nameof(RawViewModel.SelectedRowIndex))
            SyncVisualSelection(vm);

        if (e.PropertyName is null or nameof(RawViewModel.WrapWidth))
            UpdatePanRange();
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        RowsListBox.SelectionChanged -= OnSelectionChanged;
        RowsListBox.SizeChanged -= OnListBoxSizeChanged;
        RowsListBox.PropertyChanged -= OnListBoxPropertyChanged;
        PanScrollBar.ValueChanged -= OnPanValueChanged;
        DataContextChanged -= OnDataContextChanged;
        fontResourceSubscription.Dispose();

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        // Disposed synchronously here (before the content swap's trailing ItemsSource walk):
        // RawRowCollection reports empty once disposed, so that walk reads nothing.
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectionEvents)
            return;

        if (DataContext is not RawViewModel vm)
            return;

        var selectedIndex = RowsListBox.SelectedIndex;
        if (selectedIndex < 0)
            return;

        vm.SelectRow(selectedIndex);
    }

    private void SyncVisualSelection(RawViewModel vm)
    {
        if (vm.SelectedRowIndex is not int row)
        {
            RowsListBox.SelectedIndex = -1;
            return;
        }

        suppressSelectionEvents = true;
        try
        {
            RowsListBox.SelectedIndex = row >= 0 && row < vm.RowCount ? row : -1;
        }
        finally
        {
            suppressSelectionEvents = false;
        }
    }

    private void OnListBoxSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePanRange();

    private void OnContentFontChanged(object? value)
    {
        contentFontFamily = value as FontFamily;
        UpdatePanRange();
    }

    private void OnPanValueChanged(object? sender, RangeBaseValueChangedEventArgs e) => panTransform.X = -e.NewValue;

    /// <summary>
    /// Sizes the pan scrollbar from a deterministic estimate: wrap-width bytes x one measured
    /// character advance. Row text never has more chars than bytes (see RawRowReader), and "W"
    /// is a wide advance in either content font, so this is an upper bound - at the smaller
    /// wrap widths it collapses to zero and the bar hides entirely.
    /// </summary>
    private void UpdatePanRange()
    {
        double viewWidth = RowsListBox.Bounds.Width;
        if (DataContext is not RawViewModel vm || viewWidth <= 0)
        {
            HidePanBar();
            return;
        }

        double charWidth = MeasureCharWidth();
        double textViewport = Math.Max(0, viewWidth - ListBoxHorizontalPadding - LineNumberColumnWidth - WrapGutterColumnWidth);
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
        panTransform.X = 0;
    }

    private double MeasureCharWidth()
    {
        double fontSize = RowsListBox.GetValue(TextBlock.FontSizeProperty);
        var typeface = new Typeface(contentFontFamily ?? FontFamily.Default);
        var layout = new TextLayout("W", typeface, fontSize, Brushes.Black);
        return layout.WidthIncludingTrailingWhitespace;
    }
}
