using System;
using Avalonia.Controls;

namespace JsonViewerCore.Features.NdJson;

public partial class NdJsonView : UserControl
{
    private bool suppressSelectionEvents;
    private bool ignoreNextScrollChanged;

    public NdJsonView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        JsonLinesListBox.SelectionChanged += OnSelectionChanged;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateWindow();
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        JsonLinesListBox.SelectionChanged -= OnSelectionChanged;

        if (DataContext is IDisposable d)
            d.Dispose();
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
        if (ignoreNextScrollChanged)
        {
            // Console.WriteLine(
             //   $"ScrollChanged: ignored follow-up event offset={MainScrollViewer.Offset.Y}, offsetDelta={e.OffsetDelta.Y}, extentDelta={e.ExtentDelta.Y}, viewportDelta={e.ViewportDelta.Y}");
            ignoreNextScrollChanged = false;
            return;
        }

        var offsetDelta = e.OffsetDelta.Y;
        var extentDelta = e.ExtentDelta.Y;
        var viewportDelta = e.ViewportDelta.Y;

        //Console.WriteLine(
        //    $"ScrollChanged: offset={MainScrollViewer.Offset.Y}, offsetDelta={offsetDelta}, extentDelta={extentDelta}, viewportDelta={viewportDelta}");

        if (Math.Abs(offsetDelta) < double.Epsilon)
        {
          //  Console.WriteLine("ScrollChanged: ignoring non-offset scroll change.");
            return;
        }

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
            ignoreNextScrollChanged = vm.EnsureWindow(firstVisibleIndex, viewportLines);
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
