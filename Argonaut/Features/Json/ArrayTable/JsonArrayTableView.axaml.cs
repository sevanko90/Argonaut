using System;
using System.Collections.Generic;
using System.ComponentModel;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Rows;
using Argonaut.Ui.TableGrid;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Argonaut.Features.Json.ArrayTable;

/// <summary>
/// The array-table grid. Three jobs: hand the surface the view model's rows, its
/// <see cref="JsonArrayTableViewModel.Structure"/> and its route headers (which a re-shape from
/// the toolbar or a header click replaces), turn the surface's clicks into what they open - a cell
/// into the pane beside the grid, a header's link into its columns opened or collapsed - and
/// dispose the document on detach as the idempotent safety net the shell doesn't drive (window
/// close).
///
/// The grid itself - header, cells, selection, resizing, the clickable cues - is the surface's;
/// its scrollbars are driven by <see cref="RowScrollBars"/>.
/// </summary>
public partial class JsonArrayTableView : UserControl
{
    /// <summary>How wide the cell pane opens, and how wide it stays once dragged - the splitter
    /// writes the column's width, and this remembers it across the pane closing and reopening.</summary>
    private const double DefaultDetailWidth = 380;

    /// <summary>What a click on a cell does, said in the cell's own tooltip. Nothing else in a
    /// grid of values says the cells are clickable at all, so the grid's cues are this and the
    /// hover tint and mark the surface draws, and nothing more.</summary>
    private const string CellClickHint = "Click to open this cell in the detail pane";

    private const string OpensTip = "Click to expand this into its own columns";
    private const string CollapsesTip = "Click to collapse these columns back into one";

    private readonly RowScrollBars scrollBars;
    private JsonArrayTableViewModel? subscribedViewModel;
    private IReadOnlyList<JsonArrayColumnHeader>? shownHeaders;
    private double detailWidth = DefaultDetailWidth;

    public JsonArrayTableView()
    {
        InitializeComponent();

        this.scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        Surface.CellClickHint = CellClickHint;
        Surface.CellClicked += OnCellClicked;
        Surface.HeaderLinkClicked += OnHeaderLinkClicked;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.subscribedViewModel is not null)
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        this.subscribedViewModel = DataContext as JsonArrayTableViewModel;
        Surface.SelectedIndex = -1;
        this.scrollBars.ResetPan();
        if (this.subscribedViewModel is null)
        {
            ShowNothing();
            return;
        }

        this.subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowDocument(this.subscribedViewModel);
        ShowDetail(this.subscribedViewModel.HasCellDetail);
    }

    private void OnCellClicked(object? sender, TableCellPosition cell) => this.subscribedViewModel?.ShowCell(cell.Row, cell.Column);

    /// <summary>
    /// A link piece of a header: the last piece is the column's own value, so it opens what is
    /// inside it; every piece before it is a container already open around this column, so it
    /// folds that container - and these columns - back up. Inline rather than deferred: the
    /// surface has finished with the click before raising it, and there is no control under the
    /// pointer for the re-shape to pull out from under it.
    /// </summary>
    private void OnHeaderLinkClicked(object? sender, TableHeaderLink link)
    {
        if (this.subscribedViewModel is not { } vm || this.shownHeaders is not { } headers || link.Column >= headers.Count)
            return;

        var segments = headers[link.Column].Segments;
        if (link.Piece < segments.Count && segments[link.Piece].Key is { } key)
            vm.ToggleColumn(key);
    }

    private void OnCloseDetail(object? sender, RoutedEventArgs e) => this.subscribedViewModel?.CloseCellDetail();

    /// <summary>Copies the cell's own text rather than the pane's, which may carry newlines cut
    /// into a run too long to wrap.</summary>
    private async void OnCopyDetailText(object? sender, RoutedEventArgs e)
    {
        if (this.subscribedViewModel?.CellDetail?.Text is not { } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(text);
        ToastService.Show("Value copied to clipboard");
    }

    /// <summary>
    /// Opens or closes the pane's grid column. Width lives here rather than in the view model:
    /// dragging the splitter writes it, and the view model has no business knowing pixels.
    /// </summary>
    private void ShowDetail(bool visible)
    {
        var pane = Layout.ColumnDefinitions[2];
        if (visible)
        {
            pane.Width = new GridLength(this.detailWidth);
            return;
        }

        if (pane.Width.IsAbsolute && pane.Width.Value > 0)
            this.detailWidth = pane.Width.Value;

        pane.Width = new GridLength(0);
    }

    /// <summary>
    /// The view model publishes a whole new <see cref="TableStructure"/> and header list on load
    /// and on every re-shape, so the surface is handed both rather than patched - the same "a new
    /// structure replaces the old one" contract the row collection follows.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JsonArrayTableViewModel vm)
            return;

        if (e.PropertyName is null or nameof(JsonArrayTableViewModel.Structure) or nameof(JsonArrayTableViewModel.Rows))
            ShowDocument(vm);

        if (e.PropertyName is null or nameof(JsonArrayTableViewModel.HasCellDetail))
            ShowDetail(vm.HasCellDetail);
    }

    private void ShowDocument(JsonArrayTableViewModel vm)
    {
        // Rows and Structure throw until LoadAsync has published them.
        if (vm.ColumnCount == 0)
            return;

        Surface.Rows = vm.Rows;
        Surface.FitSource = vm.Rows;
        Surface.Structure = vm.Structure;

        // A new header list is new columns - even at the same count and widths, which is where
        // expanding a column can land - so it is only handed over when the view model replaced
        // it: handing over a fresh copy of the same headers would throw away every width the
        // user set.
        if (!ReferenceEquals(vm.Headers, this.shownHeaders))
        {
            this.shownHeaders = vm.Headers;
            Surface.ColumnHeaders = SurfaceHeaders(vm.Headers);
        }

        this.scrollBars.Refresh();
    }

    private void ShowNothing()
    {
        Surface.Rows = null;
        Surface.Structure = null;
        Surface.ColumnHeaders = null;
        this.shownHeaders = null;
    }

    /// <summary>
    /// A header as its route's pieces: plain text for anything with nothing inside it, a link for
    /// each container. Each link's tooltip is what says which of the two clicking it does, since
    /// the link styling alone cannot tell open from collapse; the whole route is the tip over the
    /// plain pieces.
    /// </summary>
    private static IReadOnlyList<TableColumnHeader> SurfaceHeaders(IReadOnlyList<JsonArrayColumnHeader> headers)
    {
        var shown = new TableColumnHeader[headers.Count];
        for (int c = 0; c < shown.Length; c++)
        {
            var segments = headers[c].Segments;
            var pieces = new TableHeaderPiece[segments.Count];
            for (int i = 0; i < pieces.Length; i++)
            {
                string? tip = segments[i].Key is null ? null : i == segments.Count - 1 ? OpensTip : CollapsesTip;
                pieces[i] = new TableHeaderPiece(segments[i].Text, tip);
            }

            shown[c] = new TableColumnHeader(pieces, headers[c].Display);
        }

        return shown;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        Surface.CellClicked -= OnCellClicked;
        Surface.HeaderLinkClicked -= OnHeaderLinkClicked;

        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            this.subscribedViewModel = null;
        }

        // The surface is the last thing reading the row collection.
        ShowNothing();
        this.scrollBars.Dispose();

        // Disposed synchronously here: JsonArrayRowCollection reports empty once disposed.
        if (DataContext is IDisposable d)
            d.Dispose();
    }
}
