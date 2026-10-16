using System.Collections;
using System.Collections.Specialized;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Ui.TableGrid;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Argonaut.Tests.Ui.TableGrid;

/// <summary>
/// Headless tests for <see cref="TableSurface"/>: it lays out only a screen's worth of cells
/// however far it is scrolled, and reads only the rows on screen; a click and the keys select
/// rows; a header edge drags to resize and a double-click there fits the column; the same
/// columns relabelled keep the widths the user set; the gutter is as wide as its widest label.
/// </summary>
public sealed class TableSurfaceTests
{
    /// <summary>Rows as the grid's sources present them - a read-only list that grows - counting
    /// every row read.</summary>
    private sealed class Rows(int count, int columns = 4) : IList, INotifyCollectionChanged, IColumnFitSource
    {
        private int count = count;

        public int Reads { get; private set; }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count => this.count;

        public object? this[int index]
        {
            get
            {
                Reads++;
                var cells = new TableCell[columns];
                for (int c = 0; c < columns; c++)
                    cells[c] = new TableCell(c == 0 ? "wwww" : $"r{index}c{c}");
                return new TableRow(index + 1, cells, index + 2, index * 10L);
            }
            set => throw new NotSupportedException();
        }

        public void Grow(int delta)
        {
            int start = this.count;
            this.count += delta;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new object?[delta], start));
        }

        /// <summary>The first column's values run to 30 characters among the rows read.</summary>
        public int LongestRealizedText(int columnIndex) => columnIndex == 0 ? 30 : 6;

        public IEnumerator GetEnumerator() => throw new InvalidOperationException("the grid must never walk its rows");
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

    private static TableStructure Structure(int columns, string prefix = "Column")
    {
        var names = Enumerable.Range(1, columns).Select(c => $"{prefix} {c}").ToArray();
        return TableStructure.FromMaxChars(names, Enumerable.Repeat(10, columns).ToArray());
    }

    private static async Task PumpAsync(int milliseconds = 20)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static Task Run(Func<TableSurface, Window, Rows, Task> test, int rowCount = 1_000_000, int columns = 4)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableSurfaceTests).Assembly);
        return session.Dispatch(async () =>
        {
            var rows = new Rows(rowCount, columns);
            var surface = new TableSurface { Rows = rows, FitSource = rows, Structure = Structure(columns), WidestGutterLabel = "0000000" };
            var window = new Window { Width = 800, Height = 400, Content = surface };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                await test(surface, window, rows);
                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>Paints the window so the surface draws, which is when it reads and lays out.</summary>
    private static async Task PaintAsync(Window window)
    {
        window.UpdateLayout();
        window.CaptureRenderedFrame();
        await PumpAsync();
    }

    [Fact]
    public Task Reads_and_lays_out_only_the_rows_on_screen_however_far_it_scrolls() => Run(async (surface, window, rows) =>
    {
        await PaintAsync(window);
        int screen = surface.CachedCellCount;
        Assert.InRange(screen, 1, 200);

        for (double f = 0.1; f <= 1; f += 0.1)
        {
            surface.ScrollToFraction(f);
            await PaintAsync(window);
        }

        surface.ScrollToEnd();
        await PaintAsync(window);

        Assert.InRange(surface.CachedCellCount, 1, 2 * screen);
        Assert.InRange(rows.Reads, 1, 15 * screen);
        Assert.True(surface.ShowsEnd);
    });

    [Fact]
    public Task Follows_rows_added_while_indexing() => Run(async (surface, window, rows) =>
    {
        surface.ScrollToEnd();
        double before = surface.ViewportFraction;

        rows.Grow(1_000);
        await PumpAsync();

        Assert.True(surface.ViewportFraction < before);
        Assert.True(surface.ScrollFraction > 0);
    }, rowCount: 100);

    [Fact]
    public Task A_click_selects_a_row_and_the_keys_move_it() => Run(async (surface, window, rows) =>
    {
        double y = TableSurface.HeaderHeight + 2.5 * TableSurface.RowHeight;
        window.MouseDown(new Point(surface.GutterWidth + 30, y), MouseButton.Left);
        window.MouseUp(new Point(surface.GutterWidth + 30, y), MouseButton.Left);
        await PumpAsync();
        Assert.Equal(2, surface.SelectedIndex);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, string.Empty);
        Assert.Equal(3, surface.SelectedIndex);

        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.None, string.Empty);
        Assert.Equal(rows.Count - 1, surface.SelectedIndex);
        Assert.True(surface.ShowsEnd);

        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.None, string.Empty);
        Assert.Equal(0, surface.SelectedIndex);
        Assert.Equal(0, surface.ScrollFraction);
    });

    [Fact]
    public Task A_header_edge_drags_to_resize_and_double_clicks_to_fit() => Run(async (surface, window, rows) =>
    {
        var columns = surface.Columns!;
        double edge = surface.GutterWidth + columns.Right(1);
        double before = columns.Width(1);

        window.MouseDown(new Point(edge, 10), MouseButton.Left);
        window.MouseMove(new Point(edge + 25, 10));
        window.MouseMove(new Point(edge + 60, 10));
        window.MouseUp(new Point(edge + 60, 10), MouseButton.Left);
        await PumpAsync();

        Assert.Equal(before + 60, columns.Width(1), 1);
        Assert.True(columns.IsUserSized(1));
        Assert.Equal(columns.TotalWidth, surface.WidestRowWidth, 1);

        double first = surface.GutterWidth + columns.Right(0);
        window.MouseDown(new Point(first, 10), MouseButton.Left);
        window.MouseUp(new Point(first, 10), MouseButton.Left);
        window.MouseDown(new Point(first, 10), MouseButton.Left);
        window.MouseUp(new Point(first, 10), MouseButton.Left);
        await PumpAsync();

        Assert.Equal(surface.WidthForChars(30), columns.Width(0), 1);
    });

    [Fact]
    public Task The_same_columns_relabelled_keep_the_widths_the_user_set() => Run(async (surface, window, rows) =>
    {
        var columns = surface.Columns!;
        double edge = surface.GutterWidth + columns.Right(2);
        window.MouseDown(new Point(edge, 10), MouseButton.Left);
        window.MouseMove(new Point(edge + 40, 10));
        window.MouseUp(new Point(edge + 40, 10), MouseButton.Left);
        await PumpAsync();
        double dragged = columns.Width(2);

        surface.Structure = Structure(4, "Header");
        Assert.Same(columns, surface.Columns);
        Assert.Equal(dragged, surface.Columns!.Width(2));

        surface.Structure = Structure(5);
        Assert.NotSame(columns, surface.Columns);
        Assert.False(surface.Columns!.IsUserSized(2));
    });

    [Fact]
    public Task The_gutter_is_as_wide_as_its_widest_label_and_hides_without_one() => Run(async (surface, window, rows) =>
    {
        double seven = surface.GutterWidth;

        surface.WidestGutterLabel = "4,000,000,000,000";
        Assert.True(surface.GutterWidth > seven);

        surface.LineNumbering = LineNumbering.ByLineStarts(4_000_000_000_000, 10);
        surface.WidestGutterLabel = null;
        Assert.Equal(0, surface.GutterWidth);
        await PaintAsync(window);
    });
}
