using System;
using System.ComponentModel;
using Argonaut.Features.Csv;
using Argonaut.Infrastructure;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

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
    private readonly TableGridColumns columns;
    private JsonArrayTableViewModel? subscribedViewModel;

    public JsonArrayTableView()
    {
        InitializeComponent();

        this.columns = new TableGridColumns(Table);
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
    }

    /// <summary>
    /// The view model publishes a whole new <see cref="CsvStructure"/> on load and on every
    /// re-shape, so the columns are rebuilt from it rather than patched - the same "a new
    /// structure replaces the old one" contract the row collection follows.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is JsonArrayTableViewModel vm && e.PropertyName is null or nameof(JsonArrayTableViewModel.Structure))
            RebuildColumns(vm);
    }

    private void RebuildColumns(JsonArrayTableViewModel vm)
    {
        // Structure throws until LoadAsync has published one; the first PropertyChanged for it
        // is what says the grid has a shape at all.
        if (vm.ColumnCount == 0)
            return;

        // The row collection is the fit source: a double-click on a resizer measures the rows it
        // has already realized.
        this.columns.Rebuild(vm.Structure, vm.Rows, highlightTerm: null, vm.Headers, HeaderTemplate(vm));
    }

    /// <summary>
    /// A header as its route's pieces: plain text for anything with nothing inside it, a link for
    /// each container - the last piece opening one, the pieces before it collapsing back to it.
    /// The link styling IS the affordance, which is why nothing else marks an expandable column.
    /// </summary>
    private static IDataTemplate HeaderTemplate(JsonArrayTableViewModel vm)
        => new FuncDataTemplate<JsonArrayColumnHeader>((header, _) =>
        {
            var pieces = new StackPanel { Orientation = Orientation.Horizontal };
            pieces.SetValue(ToolTip.TipProperty, header.Display);

            foreach (var segment in header.Segments)
                pieces.Children.Add(segment.Key is { } key ? Link(segment.Text, key, vm) : Plain(segment.Text));

            return pieces;
        }, supportsRecycling: false);

    private static Control Link(string text, string key, JsonArrayTableViewModel vm)
    {
        var link = new Button
        {
            Content = Plain(text),
            Classes = { "columnLink" },
            Cursor = new Cursor(StandardCursorType.Hand),
        };

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
