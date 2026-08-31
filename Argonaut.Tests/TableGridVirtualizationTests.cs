using System.Collections;
using System.Collections.Specialized;
using Argonaut.Features.Csv;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests;

/// <summary>
/// Headless UI tests for the TableView-backed grid: that it keeps a huge, lazily-realized
/// ItemsSource virtualized the way the hand-rolled ListBox grid did, that a column drag resizes
/// the column and its cells, and that double-clicking a resizer fits the column to the rows
/// <see cref="TableGridColumns"/> can see.
///
/// The virtualization half is the same class of regression RawViewVirtualizationTests guards:
/// a control that walks its whole ItemsSource materialises every row of a multi-GB file. The
/// fit-to-content half also guards a real crash - writing a column's Width from inside the
/// pointer event the resizer is still handling throws "Cannot call Measure using a size with
/// NaN values" out of TableView's layout pass, which is why the write goes through UiDeferral.
/// </summary>
public sealed class TableGridVirtualizationTests
{
    /// <summary>Same surface MemoryMappedCollectionBase presents (read-only fixed-size IList +
    /// INotifyCollectionChanged), counting every indexer hit and every enumeration so a test can
    /// tell viewport-sized realization from a whole-collection walk.</summary>
    private sealed class CountingRows : IList, INotifyCollectionChanged, IColumnFitSource
    {
        private readonly int columnCount;
        private int count;

        public CountingRows(int count, int columnCount = 3)
        {
            this.count = count;
            this.columnCount = columnCount;
        }

        /// <summary>Characters the first column's text runs to; the others stay short, so a
        /// fit-to-content gesture has something specific to land on.</summary>
        public int FirstColumnLength { get; set; } = 4;

        public int IndexerHits { get; private set; }

        public int ItemsEnumerated { get; private set; }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count => this.count;

        public object? this[int index]
        {
            get
            {
                IndexerHits++;
                var cells = new CsvCell[this.columnCount];
                for (int c = 0; c < cells.Length; c++)
                    cells[c] = new CsvCell(TextAt(index, c));
                return new CsvVisibleRow(index + 1, cells);
            }
            set => throw new NotSupportedException();
        }

        /// <summary>The growth-monitor notification shape: placeholder entries only, because the
        /// panel re-queries the indexer when it actually realizes a row.</summary>
        public void Grow(int delta)
        {
            int start = this.count;
            this.count += delta;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, new object?[delta], start));
        }

        public void Reset() => CollectionChanged?.Invoke(this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        private string TextAt(int rowIndex, int columnIndex)
            => columnIndex == 0
                ? new string('w', FirstColumnLength)
                : $"r{rowIndex}c{columnIndex}";

        /// <summary>Stands in for JsonArrayRowCollection's realized-row cache.</summary>
        public int LongestRealizedText(int columnIndex)
            => TextAt(0, columnIndex).Length;

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < this.count; i++)
            {
                ItemsEnumerated++;
                yield return this[i];
            }
        }

        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object? value) => false;
        public int IndexOf(object? value) => -1;
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
    }

    private static async Task PumpAsync(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A structure of <paramref name="columnCount"/> evenly-widthed columns: these
    /// tests care about names and count, and set the widths they check by dragging.</summary>
    private static CsvStructure StructureOf(int columnCount)
    {
        var names = new string[columnCount];
        var chars = new int[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            names[c] = $"Column {c + 1}";
            chars[c] = 20;
        }

        return CsvStructure.FromMaxChars(names, chars);
    }

    private static (TableView Table, TableGridColumns Columns) BuildTable(CountingRows rows, CsvStructure structure)
    {
        var table = new TableView { ItemsSource = rows, SelectionMode = SelectionMode.Single, CanUserResizeColumns = true };
        var columns = new TableGridColumns(table);
        columns.Rebuild(structure, rows);
        return (table, columns);
    }

    /// <summary>The x of a column's PART_Resizer, which sits on the column's right edge.</summary>
    private static double ResizerX(Window window, TableView table, TableViewColumn column)
    {
        double x = window.GetVisualDescendants()
            .First(v => v.GetType().Name.Contains("ColumnHeadersPresenter")).Bounds.X;

        foreach (var other in table.Columns)
        {
            x += other.ActualWidth;
            if (ReferenceEquals(other, column))
                break;
        }

        return x;
    }

    private static async Task DragResizerAsync(Window window, TableViewColumn column, double delta)
    {
        var table = (TableView)((Window)window).GetVisualDescendants().First(v => v is TableView);
        double from = ResizerX(window, table, column);
        double to = from + delta;
        double step = delta > 0 ? 40 : -40;

        window.MouseDown(new Point(from, 18), MouseButton.Left, RawInputModifiers.None);
        await PumpAsync(10);
        for (double x = from; Math.Abs(x - from) < Math.Abs(delta); x += step)
        {
            window.MouseMove(new Point(x, 18), RawInputModifiers.None);
            await PumpAsync(5);
        }
        window.MouseMove(new Point(to, 18), RawInputModifiers.None);
        await PumpAsync(5);
        window.MouseUp(new Point(to, 18), MouseButton.Left, RawInputModifiers.None);
        await PumpAsync(10);
        window.UpdateLayout();
    }

    private static async Task DoubleClickResizerAsync(Window window, TableViewColumn column)
    {
        var table = (TableView)window.GetVisualDescendants().First(v => v is TableView);
        var at = new Point(ResizerX(window, table, column), 18);

        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        await PumpAsync();
        window.UpdateLayout();
    }

    private static double FirstCellWidth(Window window)
        => window.GetVisualDescendants().First(v => v.GetType().Name == "TableViewCell").Bounds.Width;

    [Fact]
    public Task TableView_OverHugeLazySource_RealizesOnlyTheViewport()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new CountingRows(5_000_000);
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 900, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                int afterBind = rows.IndexerHits;
                Assert.InRange(afterBind, 1, 500);
                Assert.InRange(window.GetVisualDescendants().OfType<TableViewRow>().Count(), 1, 200);

                // The index-path cell bindings must actually resolve against CsvVisibleRow.
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("r0c1", texts);
                Assert.Contains("Column 1", texts);

                // A growth tick: placeholder Adds must cost nothing until rows are realized.
                rows.Grow(1_000_000);
                await PumpAsync();
                window.UpdateLayout();
                Assert.Equal(afterBind, rows.IndexerHits);

                // A re-shape Reset re-realizes the viewport, not the collection.
                rows.Reset();
                await PumpAsync();
                window.UpdateLayout();
                Assert.InRange(rows.IndexerHits, afterBind, afterBind + 500);

                // Nothing may ever walk the collection end to end.
                Assert.Equal(0, rows.ItemsEnumerated);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task ColumnDrag_ResizesTheColumnAndItsCells()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new CountingRows(100_000);
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 1_400, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var column = table.Columns[0];
                double initial = column.ActualWidth;

                await DragResizerAsync(window, column, +200);
                Assert.Equal(initial + 200, column.ActualWidth, 1);
                Assert.Equal(column.ActualWidth, FirstCellWidth(window), 1);

                await DragResizerAsync(window, column, -120);
                Assert.Equal(initial + 80, column.ActualWidth, 1);
                Assert.Equal(column.ActualWidth, FirstCellWidth(window), 1);

                // No ceiling: a wide drag lands where the pointer stopped rather than springing
                // back, which is what the clamp used to do and what looked wrong doing it.
                await DragResizerAsync(window, column, +600);
                Assert.Equal(initial + 680, column.ActualWidth, 1);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task DoubleClickingAResizer_FitsTheColumnToItsRealizedRows()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new CountingRows(100_000) { FirstColumnLength = 40 };
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 1_400, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var column = table.Columns[0];
                await DragResizerAsync(window, column, -60);
                double narrowed = column.ActualWidth;
                Assert.True(narrowed < CsvStructure.WidthForChars(40),
                    $"the column must start too narrow for its content: narrowed={narrowed} fit={CsvStructure.WidthForChars(40)} " +
                    $"advance={CellTextMetrics.Current.CharAdvance} inset={CellTextMetrics.Current.CellInset}");

                await DoubleClickResizerAsync(window, column);
                Assert.Equal(CsvStructure.WidthForChars(40), column.ActualWidth, 1);
                Assert.Equal(column.ActualWidth, FirstCellWidth(window), 1);

                // A column whose content is shorter than its header fits the header instead.
                var third = table.Columns[2];
                await DoubleClickResizerAsync(window, third);
                Assert.Equal(CsvStructure.WidthForChars("Column 3".Length), third.ActualWidth, 1);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task Rebuild_ReplacesColumnsAndReseedsWidths()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new CountingRows(1_000, columnCount: 5);
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 900, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                Assert.Equal(3, table.Columns.Count);

                columns.Rebuild(StructureOf(5));
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal(5, table.Columns.Count);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("Column 5", texts);
                Assert.Contains("r0c4", texts);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task SeededWidth_FitsTheTextItWasMeasuredFrom()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            // A column widthed for its content must actually render that content untrimmed. The
            // budget has been short twice: once because the cell template added a 12px margin the
            // heuristic knew nothing about, once because 7px per character undercounts a 12px
            // monospace advance. Both showed up as "66317..." in a column sized for "6631700".
            // (Headless shapes every glyph at a fixed advance, so this guards the budget
            // arithmetic - chrome plus per-character - not any real font's metrics.)
            var rows = new CountingRows(1_000) { FirstColumnLength = 24 };
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 1_400, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                // Fit the first column to its content, then check the text is not elided.
                await DoubleClickResizerAsync(window, table.Columns[0]);

                var cell = window.GetVisualDescendants().First(v => v.GetType().Name == "TableViewCell");
                var text = cell.GetVisualDescendants().OfType<TextBlock>().First();

                Assert.Equal(new string('w', 24), text.Text);
                Assert.True(text.DesiredSize.Width <= text.Bounds.Width + 0.5,
                    $"text wants {text.DesiredSize.Width} but got {text.Bounds.Width} in a {table.Columns[0].ActualWidth} column");
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task CellInsetIsLearnedFromARealizedCell()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new CountingRows(1_000);
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 900, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                // Nothing but a realized cell knows its chrome - the theme's padding is a dynamic
                // resource and an unattached cell never applies its theme - so the metrics learn
                // it from the first one drawn rather than carrying a hard-coded guess.
                var cell = window.GetVisualDescendants().OfType<TableViewCell>().First();
                double inset = cell.Padding.Left + cell.Padding.Right + cell.BorderThickness.Left + cell.BorderThickness.Right;

                Assert.True(inset > 0, "the cell theme should inset its content");
                Assert.Equal(inset, CellTextMetrics.Current.CellInset, 1);
                Assert.True(CellTextMetrics.Current.CharAdvance > 0, "the content font's advance must be measured");

                // And the columns account for it: a column seeded for N characters gives those
                // characters the whole width the metrics measured for them.
                Assert.Equal(CsvStructure.WidthForChars(20), table.Columns[0].ActualWidth, 1);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task ContentFontChange_ReseedsUntouchedColumnsAndLeavesResizedOnesAlone()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            // The status bar can repoint the content font while a grid is showing. Widths were
            // measured for the outgoing face, so they have to follow it - except where the user
            // has said what a column's width should be.
            var rows = new CountingRows(1_000);
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 1_400, Height = 600, Content = table };
            object? originalFontSize = Application.Current!.Resources["AppContentFontSize"];
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                await DragResizerAsync(window, table.Columns[0], +150);
                double resized = table.Columns[0].Width.Value;
                double untouched = table.Columns[1].Width.Value;

                Application.Current.Resources["AppContentFontSize"] = 24.0;
                await PumpAsync();
                window.UpdateLayout();

                Assert.True(table.Columns[1].Width.Value > untouched,
                    $"a column at its discovered width should follow the font: {untouched} -> {table.Columns[1].Width.Value}");
                Assert.Equal(resized, table.Columns[0].Width.Value, 1);
                return true;
            }
            finally
            {
                Application.Current.Resources["AppContentFontSize"] = originalFontSize;
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task HighlightTerm_LightsUpMatchesInCellsAndHeaders()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            // The CSV grid highlights the find term wherever it shows, header included. The
            // highlight path builds Inlines instead of plain Text, which is what this checks -
            // the term is in "Column 1" and in every first-column cell below.
            var rows = new CountingRows(100) { FirstColumnLength = 8 };
            var table = new TableView { ItemsSource = rows, SelectionMode = SelectionMode.Single };
            var columns = new TableGridColumns(table);
            var term = new HighlightSource { Term = "w" };
            columns.Rebuild(StructureOf(3), rows, new Binding(nameof(HighlightSource.Term)) { Source = term });

            var window = new Window { Width = 900, Height = 400, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var highlighted = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Inlines is { Count: > 1 })
                    .ToList();

                Assert.NotEmpty(highlighted);

                term.Term = null;
                await PumpAsync();
                window.UpdateLayout();

                Assert.Empty(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Inlines is { Count: > 1 }));
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>Stands in for the view model a CSV grid binds its find term from.</summary>
    private sealed class HighlightSource : System.ComponentModel.INotifyPropertyChanged
    {
        private string? term;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string? Term
        {
            get => this.term;
            set
            {
                this.term = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Term)));
            }
        }
    }

    [Fact]
    public Task RelabellingColumns_KeepsTheWidthsTheUserSet()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            // CSV's "first row is header" tickbox republishes the same columns under different
            // names. That is a relabelling, not a re-shape, so a width the user dragged survives.
            var rows = new CountingRows(1_000);
            var (table, columns) = BuildTable(rows, StructureOf(3));
            var window = new Window { Width = 1_400, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                await DragResizerAsync(window, table.Columns[0], +150);
                double resized = table.Columns[0].Width.Value;

                columns.Rebuild(StructureOf(3).WithNames(["First", "Second", "Third"]), rows);
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal("First", table.Columns[0].Header);
                Assert.Equal(resized, table.Columns[0].Width.Value, 1);

                // A real re-shape (different column count) does start over.
                columns.Rebuild(StructureOf(4), rows);
                await PumpAsync();
                window.UpdateLayout();

                Assert.Equal(4, table.Columns.Count);
                Assert.Equal(CsvStructure.WidthForChars(20), table.Columns[0].Width.Value, 1);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>A header content object rendered as a clickable link, the shape the JSON array
    /// table's route headers take.</summary>
    private sealed record HeaderLabel(string Text);

    private static Avalonia.Controls.Templates.IDataTemplate LinkHeaders(Action<string> clicked)
        => new Avalonia.Controls.Templates.FuncDataTemplate<HeaderLabel>((label, _) =>
        {
            var link = new Button { Content = new TextBlock { Text = label.Text } };
            link.Click += (_, _) => clicked(label.Text);
            return link;
        }, supportsRecycling: false);

    [Fact]
    public Task NewHeaders_RebuildTheColumnsEvenWhenTheMeasuredShapeIsIdentical()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new CountingRows(1_000);
            var table = new TableView { ItemsSource = rows, SelectionMode = SelectionMode.Single };
            var columns = new TableGridColumns(table);

            var before = new object[] { new HeaderLabel("geometry"), new HeaderLabel("b"), new HeaderLabel("c") };
            columns.Rebuild(StructureOf(3), rows, highlightTerm: null, before, LinkHeaders(_ => { }));

            var window = new Window { Width = 900, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                // Same column count and same discovered widths, but these are DIFFERENT columns -
                // an expansion that happens to measure the same. Taking the relabel shortcut here
                // would leave every cell template bound to the previous column's index.
                var after = new object[] { new HeaderLabel("geometry.type"), new HeaderLabel("b"), new HeaderLabel("c") };
                columns.Rebuild(StructureOf(3), rows, highlightTerm: null, after, LinkHeaders(_ => { }));
                await PumpAsync();
                window.UpdateLayout();

                Assert.Same(after[0], table.Columns[0].Header);
                Assert.Contains("geometry.type", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

                // Publishing the SAME shape again is the relabel path - which must leave header
                // content alone rather than writing the structure's plain name over it.
                columns.Rebuild(StructureOf(3), rows, highlightTerm: null, after, LinkHeaders(_ => { }));
                await PumpAsync();
                window.UpdateLayout();

                Assert.Same(after[0], table.Columns[0].Header);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task ClickingAHeaderLink_ReachesItsHandler()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new CountingRows(1_000);
            var table = new TableView { ItemsSource = rows, SelectionMode = SelectionMode.Single };
            var columns = new TableGridColumns(table);

            string? clicked = null;
            var headers = new object[] { new HeaderLabel("geometry"), new HeaderLabel("b"), new HeaderLabel("c") };
            columns.Rebuild(StructureOf(3), rows, highlightTerm: null, headers, LinkHeaders(text => clicked = text));

            var window = new Window { Width = 900, Height = 600, Content = table };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var link = window.GetVisualDescendants().OfType<Button>()
                    .First(b => b.Content is TextBlock { Text: "geometry" });
                var at = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("The header link is not in the window's visual tree.");

                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();

                Assert.Equal("geometry", clicked);
                return true;
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }
}
