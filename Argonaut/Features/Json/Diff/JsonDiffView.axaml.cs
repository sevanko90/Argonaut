using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// The diff: a <see cref="TreeSurface"/> over the view model's <see cref="JsonDiffViewModel.Tree"/>
/// - the left document at first, then the merged tree drawn two panes wide - with its scrollbar,
/// and the source/target context bar under it.
/// </summary>
public partial class JsonDiffView : UserControl
{
    /// <summary>The washes behind a pane: low-alpha overlays that read on light and dark themes
    /// alike, painted per pane so an added row tints only where its content is.</summary>
    private static readonly IReadOnlyDictionary<TreeRowTint, IBrush> Tints = new Dictionary<TreeRowTint, IBrush>
    {
        [TreeRowTint.Added] = new ImmutableSolidColorBrush(Color.Parse("#2E4CAF50")),
        [TreeRowTint.Removed] = new ImmutableSolidColorBrush(Color.Parse("#2EF44336")),
        [TreeRowTint.Changed] = new ImmutableSolidColorBrush(Color.Parse("#2EFFC107")),
        [TreeRowTint.Moved] = new ImmutableSolidColorBrush(Color.Parse("#2E2196F3")),
    };

    /// <summary>The mark before a pair on the way to a change.</summary>
    private static readonly IBrush ChangeMark = new ImmutableSolidColorBrush(Color.Parse("#FFC107"));

    private readonly RowScrollBars scrollBars;
    private JsonDiffViewModel? subscribedViewModel;

    public JsonDiffView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        ActualThemeVariantChanged += OnThemeChanged;

        Surface.SelectionChanged += OnSurfaceSelectionChanged;
        Surface.SizeChanged += OnSurfaceSizeChanged;
        Surface.TintBrushes = Tints;
        scrollBars = new RowScrollBars(Surface, VerticalScrollBar, pan: null);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyRunBrushes();
        scrollBars.Refresh();
        ApplyPendingReveal();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyRunBrushes();

    /// <summary>The JSON tree's palette, plus the change mark.</summary>
    private void ApplyRunBrushes()
    {
        JsonTreePalette.Apply(Surface, this);
        var brushes = new Dictionary<TreeRunStyle, IBrush>(Surface.RunBrushes ?? new Dictionary<TreeRunStyle, IBrush>())
        {
            [TreeRunStyle.Change] = ChangeMark,
        };
        Surface.RunBrushes = brushes;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Loaded -= OnLoaded;
        DataContextChanged -= OnDataContextChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        Surface.SelectionChanged -= OnSurfaceSelectionChanged;
        Surface.SizeChanged -= OnSurfaceSizeChanged;
        scrollBars.Dispose();
        Unsubscribe();

        // Let go of the rows before the view model releases the bytes they read. Disposed here,
        // like the JSON view, as well as by the shell; idempotent.
        Surface.Document = null;
        if (DataContext is IDisposable d)
            d.Dispose();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        if (DataContext is JsonDiffViewModel vm)
        {
            subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.RevealRequested += OnRevealRequested;
            vm.ChangesOnlyChanged += OnChangesOnlyChanged;
            Surface.Document = vm.Tree;
            Surface.HighlightTerm = vm.HighlightTerm;
            ApplyPendingReveal();
        }
        else
        {
            Surface.Document = null;
        }

        scrollBars.Refresh();
    }

    private void Unsubscribe()
    {
        if (subscribedViewModel is null)
            return;

        subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        subscribedViewModel.RevealRequested -= OnRevealRequested;
        subscribedViewModel.ChangesOnlyChanged -= OnChangesOnlyChanged;
        subscribedViewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JsonDiffViewModel vm)
            return;

        if (e.PropertyName is null or nameof(JsonDiffViewModel.HighlightTerm))
            Surface.HighlightTerm = vm.HighlightTerm;

        if (e.PropertyName is null or nameof(JsonDiffViewModel.Tree))
        {
            Surface.Document = vm.Tree;
            scrollBars.Refresh();
            ApplyPendingReveal();
        }
    }

    private void OnRevealRequested(object? sender, EventArgs e) => ApplyPendingReveal();

    private void OnChangesOnlyChanged(object? sender, EventArgs e) => Surface.Reseat();

    /// <summary>Shows the view model's pending reveal once the surface can: it needs the merged
    /// tree and a height to centre the row in.</summary>
    private void ApplyPendingReveal()
    {
        if (subscribedViewModel is not { PendingReveal: { } key } vm
            || !ReferenceEquals(Surface.Document, vm.DiffTree) || Surface.Bounds.Height <= 0)
        {
            return;
        }

        vm.ClearPendingReveal();
        Surface.Reveal(key, expandAncestors: true);
    }

    private void OnSurfaceSelectionChanged(object? sender, EventArgs e)
        => subscribedViewModel?.OnRowSelected(Surface.SelectedRow);

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyPendingReveal();

    private void OnToggleSourceMode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JsonDiffViewModel vm)
            vm.ToggleSourceMode();
    }

    private void OnToggleTargetMode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JsonDiffViewModel vm)
            vm.ToggleTargetMode();
    }

    private async void OnCopySourceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JsonDiffViewModel vm)
            return;

        await CopyToClipboardAsync(vm.SourcePrefix + vm.SourceChanged + vm.SourceSuffix);
    }

    private async void OnCopyTargetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JsonDiffViewModel vm)
            return;

        await CopyToClipboardAsync(vm.TargetPrefix + vm.TargetChanged + vm.TargetSuffix);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(text);
        ToastService.Show("Copied to clipboard");
    }
}
