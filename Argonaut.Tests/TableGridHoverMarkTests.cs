using System.Collections;
using System.Collections.Specialized;
using Argonaut.Features.Csv;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests;

/// <summary>
/// The "this cell opens something" mark lives in the adorner layer, one per grid, rather than
/// inside every realized cell - see docs/json-array-table-scroll-perf.md for why. These guard
/// both halves of that: that a cell is still a single control (the thing the change bought), and
/// that the mark still actually appears on the cell under the pointer (the thing it must not
/// have cost).
///
/// These need no awaiting inside the body, so they use the synchronous `Dispatch(Action, ...)`
/// overload - but they still RETURN its task, because a dispatch body whose task nobody observes
/// swallows everything it throws, assertion failures included. The sibling headless tests get the
/// same guarantee the other way, by ending their async body with `return true;` so it binds to
/// Dispatch&lt;T&gt;(Func&lt;Task&lt;T&gt;&gt;). Either is fine; dropping the task is not. See
/// docs/headless-test-dispatch-hole.md.
/// </summary>
public sealed class TableGridHoverMarkTests
{
    private const string ClickHint = "Click to open this value";

    private sealed class Rows : IList, INotifyCollectionChanged, IColumnFitSource
    {
        private readonly int columnCount;

        public Rows(int count, int columnCount)
        {
            Count = count;
            this.columnCount = columnCount;
        }

        public event NotifyCollectionChangedEventHandler? CollectionChanged { add { } remove { } }

        public int Count { get; }

        public object? this[int index]
        {
            get
            {
                var cells = new CsvCell[this.columnCount];
                for (int c = 0; c < cells.Length; c++)
                    cells[c] = new CsvCell($"r{index}c{c}");
                return new CsvVisibleRow(index + 1, cells);
            }
            set => throw new NotSupportedException();
        }

        public int LongestRealizedText(int columnIndex) => 6;
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
        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return this[i];
        }
    }

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

    /// <summary>Runs <paramref name="body"/> on the headless UI thread and lets its exceptions -
    /// assertion failures included - reach the runner.</summary>
    private static Task OnUiThread(Action<Window, TableGridColumns> body, string? clickHint)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TableGridHoverMarkTests).Assembly);

        // RETURNED, not fire-and-forget: Dispatch swallows whatever the body throws unless the
        // task it hands back is the one the runner awaits. Verified by mutation - an Assert.Fail
        // in a body whose task is dropped is reported as a PASS.
        return session.Dispatch(() =>
        {
            var rows = new Rows(500, 4);
            var table = new TableView { ItemsSource = rows, SelectionMode = SelectionMode.Single };

            // The one thing the view's own styles contribute: a cell with no background is not
            // hit-tested, so it never becomes the thing under the pointer and never marks.
            table.Styles.Add(new Style(x => x.OfType<TableViewCell>())
            {
                Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent) },
            });

            var columns = new TableGridColumns(table);
            columns.Rebuild(StructureOf(4), rows, highlightTerm: null, headers: null,
                headerTemplate: null, clickHint: clickHint);

            var window = new Window { Width = 900, Height = 600, Content = table };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                body(window, columns);
            }
            finally
            {
                columns.Dispose();
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static Border? MarkIn(Window window)
        => window.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Child is Avalonia.Controls.Shapes.Path);

    [Fact]
    public Task ClickableCell_HoldsASingleControl_NotAPanelWrappingAMark()
    {
        return OnUiThread((window, _) =>
        {
            var cell = window.GetVisualDescendants().First(v => v.GetType().Name == "TableViewCell");

            // The cell's content is the text block itself. A panel here would mean the mark is
            // back inside every cell, which is the regression this guards.
            Assert.Empty(cell.GetVisualDescendants().OfType<Panel>()
                .Where(p => p.GetVisualChildren().OfType<TextBlock>().Any()));
            Assert.NotEmpty(cell.GetVisualDescendants().OfType<TextBlock>());
        }, ClickHint);
    }

    [Fact]
    public Task NoMarkIsRealized_UntilThePointerIsOverACell()
    {
        return OnUiThread((window, _) => Assert.Null(MarkIn(window)), ClickHint);
    }

    [Fact]
    public Task HoveringACell_PutsTheMarkOnIt()
    {
        return OnUiThread((window, _) =>
        {
            var cell = (Visual)window.GetVisualDescendants().First(v => v.GetType().Name == "TableViewCell");
            var centre = cell.Bounds.Center;
            var inWindow = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)
                ?? new Point(centre.X, centre.Y);

            window.MouseMove(inWindow, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var mark = MarkIn(window);
            Assert.NotNull(mark);
            Assert.Same(cell, AdornerLayer.GetAdornedElement(mark!));

            // A press on the mark must land on the cell it is marking, not be swallowed by it.
            Assert.False(mark!.IsHitTestVisible);
        }, ClickHint);
    }

    [Fact]
    public Task AGridWithoutAClickHint_NeverGetsAMark()
    {
        return OnUiThread((window, _) =>
        {
            var cell = (Visual)window.GetVisualDescendants().First(v => v.GetType().Name == "TableViewCell");
            var inWindow = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)
                ?? default;

            window.MouseMove(inWindow, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Null(MarkIn(window));
        }, clickHint: null);
    }
}
