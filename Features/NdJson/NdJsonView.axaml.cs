using System;
using Avalonia.Controls;

namespace JsonViewerCore.Features.NdJson;

public partial class NdJsonView : UserControl
{
    private bool suppressSelectionEvents;

    public NdJsonView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        JsonLinesListBox.SelectionChanged += OnSelectionChanged;
    }

    private NdJsonViewModel? subscribedViewModel;

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is NdJsonViewModel vm && subscribedViewModel != vm)
        {
            subscribedViewModel = vm;
            vm.WindowApplied += OnWindowApplied;
        }

        UpdateWindow();
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        JsonLinesListBox.SelectionChanged -= OnSelectionChanged;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.WindowApplied -= OnWindowApplied;
            subscribedViewModel = null;
        }

        if (DataContext is IDisposable d)
            d.Dispose();
    }

    // A window loaded in the background has just been applied on the UI thread; the
    // items were replaced wholesale, so restore the selection highlight for the row.
    private void OnWindowApplied()
    {
        if (DataContext is not NdJsonViewModel vm)
            return;

        suppressSelectionEvents = true;
        try
        {
            SyncVisualSelection(vm);
        }
        finally
        {
            suppressSelectionEvents = false;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectionEvents)
            return;

        if (DataContext is not NdJsonViewModel vm)
            return;

        var selectedIndex = JsonLinesListBox.SelectedIndex;
        if (selectedIndex < 0)
            return;

        vm.LoadSelectedLine(vm.CurrentWindowStart + selectedIndex);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Only react to genuine offset changes. Re-layout caused by loading a new
        // window keeps the offset fixed (the extent is constant), so those follow-up
        // events have offsetDelta ~= 0 and are ignored here. EnsureWindow is idempotent,
        // so there is no need to suppress the "next" event — doing so would drop real
        // scroll movements while dragging the scrollbar and leave the window stranded.
        if (Math.Abs(e.OffsetDelta.Y) < double.Epsilon)
            return;

        UpdateWindow();
    }

    private void UpdateWindow()
    {
        if (DataContext is not NdJsonViewModel vm)
            return;

        const double lineHeight = 20;
        var viewportLines = (int)Math.Ceiling(MainScrollViewer.Viewport.Height / lineHeight);
        if (viewportLines <= 0)
            viewportLines = 50;

        var firstVisibleIndex = Math.Max(0, (int)Math.Floor(MainScrollViewer.Offset.Y / lineHeight));
        suppressSelectionEvents = true;
        try
        {
            vm.EnsureWindow(firstVisibleIndex, viewportLines);
            SyncVisualSelection(vm);
        }
        finally
        {
            suppressSelectionEvents = false;
        }
    }

    private void SyncVisualSelection(NdJsonViewModel vm)
    {
        if (vm.SelectedLineNumber is null)
        {
            JsonLinesListBox.SelectedIndex = -1;
            return;
        }

        var selectedIndex = vm.SelectedLineNumber.Value - 1 - vm.CurrentWindowStart;
        JsonLinesListBox.SelectedIndex = selectedIndex >= 0 && selectedIndex < vm.VisibleLines.Count
            ? selectedIndex
            : -1;
    }
}
