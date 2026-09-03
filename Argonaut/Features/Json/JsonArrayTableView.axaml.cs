using System;
using System.ComponentModel;
using Argonaut.Features.Csv;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Argonaut.Features.Json;

/// <summary>
/// The array-table grid. Two jobs: keep the TableView's columns in step with the view model's
/// <see cref="JsonArrayTableViewModel.Structure"/> (which a re-shape from the toolbar replaces),
/// and dispose the document on detach as the idempotent safety net the shell doesn't drive
/// (window close).
///
/// The headers are built here too: a column's header is its route spelled out as pieces, and the
/// pieces naming a container are links that open or collapse it. The click hands the work to
/// <see cref="UiDeferral.AfterCurrentInput"/> rather than doing it inline - it replaces
/// TableView.Columns, and the button that raised it lives inside one of those very columns'
/// headers (see CLAUDE.md on re-entrant input paths).
///
/// The sticky header, its horizontal-scroll tracking and the column resizer all belong to
/// TableView now; what used to be mirrored by hand here is gone.
///
/// No column scroll-into-view counterpart, because that exists for search reveals and this
/// document has no search navigator in v1.
/// </summary>
public partial class JsonArrayTableView : UserControl
{
    /// <summary>How wide the cell pane opens, and how wide it stays once dragged - the splitter
    /// writes the column's width, and this remembers it across the pane closing and reopening.</summary>
    private const double DefaultDetailWidth = 380;

    /// <summary>What a click on a cell does, said in the cell's own tooltip. Nothing else in a
    /// grid of values says the cells are clickable at all, so the grid's cues are this, the hover
    /// mark and tint TableGridColumns and the styles draw, and nothing more.</summary>
    private const string CellClickHint = "Click to open this cell in the detail pane";

    private readonly TableGridColumns columns;
    private JsonArrayTableViewModel? subscribedViewModel;
    private double detailWidth = DefaultDetailWidth;

    public JsonArrayTableView()
    {
        InitializeComponent();

        this.columns = new TableGridColumns(Table);
        Table.AddHandler(PointerPressedEvent, OnTablePointerPressed, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.subscribedViewModel is not null)
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        this.subscribedViewModel = DataContext as JsonArrayTableViewModel;
        if (this.subscribedViewModel is null)
            return;

        this.subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildColumns(this.subscribedViewModel);
        ShowDetail(this.subscribedViewModel.HasCellDetail);
    }

    /// <summary>
    /// Which cell was clicked. The cell control carries its own column, which is the reliable
    /// answer; a click that lands on the row instead - an empty cell has no text to hit - falls
    /// back to measuring the pointer against the columns' widths, so a missing property is still
    /// a cell the reader can ask about.
    /// </summary>
    private void OnTablePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (this.subscribedViewModel is not { } vm || e.ClickCount != 1)
            return;

        if (e.Source is not Visual source || source.FindAncestorOfType<TableViewRow>() is not { } row)
            return;

        int rowIndex = Table.IndexFromContainer(row);
        if (rowIndex < 0)
            return;

        int column = source.FindAncestorOfType<TableViewCell>() is { Column: { } cell }
            ? Table.Columns.IndexOf(cell)
            : ColumnAt(e.GetPosition(row).X);

        if (column >= 0)
            vm.ShowCell(rowIndex, column);
    }

    /// <summary>The column an x offset inside a row falls in, or -1 past the last one.</summary>
    private int ColumnAt(double x)
    {
        double edge = 0;
        for (int c = 0; c < Table.Columns.Count; c++)
        {
            edge += Table.Columns[c].ActualWidth;
            if (x < edge)
                return c;
        }

        return -1;
    }

    private void OnCloseDetail(object? sender, RoutedEventArgs e) => this.subscribedViewModel?.CloseCellDetail();

    private void OnDetailToggleExpandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: JsonRow row })
            this.subscribedViewModel?.CellDetail?.Rows?.ToggleExpand(row.Position);
    }

    private void OnDetailRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is JsonRow row)
            this.subscribedViewModel?.CellDetail?.Rows?.ToggleExpand(row.Position);
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
    /// The view model publishes a whole new <see cref="CsvStructure"/> on load and on every
    /// re-shape, so the columns are rebuilt from it rather than patched - the same "a new
    /// structure replaces the old one" contract the row collection follows.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JsonArrayTableViewModel vm)
            return;

        if (e.PropertyName is null or nameof(JsonArrayTableViewModel.Structure))
            RebuildColumns(vm);

        if (e.PropertyName is null or nameof(JsonArrayTableViewModel.HasCellDetail))
            ShowDetail(vm.HasCellDetail);
    }

    private void RebuildColumns(JsonArrayTableViewModel vm)
    {
        // Structure throws until LoadAsync has published one; the first PropertyChanged for it
        // is what says the grid has a shape at all.
        if (vm.ColumnCount == 0)
            return;

        // The row collection is the fit source: a double-click on a resizer measures the rows it
        // has already realized.
        this.columns.Rebuild(vm.Structure, vm.Rows, highlightTerm: null, vm.Headers, HeaderTemplate(vm), CellClickHint);
    }

    /// <summary>
    /// A header as its route's pieces: plain text for anything with nothing inside it, a link for
    /// each container - the last piece opening one, the pieces before it collapsing back to it.
    /// The link styling is the affordance; each link's own tooltip is what says which of the two
    /// clicking it does, since accent-and-underline alone cannot tell open from collapse.
    /// The whole route stays on the panel, so hovering the plain pieces still gives the path.
    /// </summary>
    private static IDataTemplate HeaderTemplate(JsonArrayTableViewModel vm)
        => new FuncDataTemplate<JsonArrayColumnHeader>((header, _) =>
        {
            var pieces = new StackPanel { Orientation = Orientation.Horizontal };
            pieces.SetValue(ToolTip.TipProperty, header.Display);

            for (int i = 0; i < header.Segments.Count; i++)
            {
                var segment = header.Segments[i];

                // The last piece is this column's own value, so its link opens what is inside it;
                // every piece before it is a container already open around this column, so its
                // link folds that container - and these columns - back up.
                pieces.Children.Add(segment.Key is { } key
                    ? Link(segment.Text, key, vm, i == header.Segments.Count - 1)
                    : Plain(segment.Text));
            }

            return pieces;
        }, supportsRecycling: false);

    private static Control Link(string text, string key, JsonArrayTableViewModel vm, bool opens)
    {
        // The link's own text carries the class the accent-and-underline styling selects on.
        // A descendant selector would reach further than it looks: a tooltip's content is
        // parented under the control it belongs to, so "Button.columnLink TextBlock" styled the
        // tip that explains the link as though it were another link.
        var label = Plain(text);
        label.Classes.Add("columnLinkText");

        var link = new Button
        {
            Content = label,
            Classes = { "columnLink" },
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        link.SetValue(ToolTip.TipProperty, opens
            ? "Click to expand this into its own columns"
            : "Click to collapse these columns back into one");

        // Deferred: this replaces the columns collection the clicked button is sitting inside.
        link.Click += (_, _) => UiDeferral.AfterCurrentInput(() => vm.ToggleColumn(key));
        return link;
    }

    private static TextBlock Plain(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
        block.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("AppContentFontFamily"));
        return block;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        Table.RemoveHandler(PointerPressedEvent, OnTablePointerPressed);

        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            this.subscribedViewModel = null;
        }

        // Columns first: their cell bindings are the last things reading the row collection.
        this.columns.Dispose();

        // Disposed synchronously here (before the content swap's trailing ItemsSource walk):
        // JsonArrayRowCollection reports empty once disposed, so that walk reads nothing.
        if (DataContext is IDisposable d)
            d.Dispose();
    }
}
