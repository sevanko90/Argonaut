using Argonaut.Engine.Indexing.Trees;
using Argonaut.Tests.Support;
using Argonaut.Ui.Rows;
using Argonaut.Ui.Tree;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Argonaut.Tests.Ui.Tree;

/// <summary>
/// The generic tree surface in a real headless window, over the test-only S-expression format so
/// nothing here leans on JSON. The surface decides its rows during layout, which is what makes
/// them observable without a renderer.
///
/// Harness rule (docs/headless-test-dispatch-hole.md): each dispatch body ends with
/// <c>return true;</c> and the test returns the dispatch task, or assertions after the first
/// await run unobserved.
/// </summary>
public sealed class TreeSurfaceTests
{
    private const double WindowHeight = 440;
    private const int FullRows = (int)(WindowHeight / RowSurface.RowHeight);

    private static async Task PumpAsync()
    {
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key) => window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, string.Empty);

    private sealed record Harness(Window Window, TreeSurface Surface, byte[] Bytes, List<TreeRow> Rows, TreeDocument Document);

    /// <summary>A surface over a generated document, and every row the document shows at the
    /// given default depth, from a cursor walk - the reference the surface is held to.</summary>
    private static Task WithSurface(int defaultDepth, Func<Harness, Task> body, int topLevelChildren = 200,
        Func<byte[], ITreeRowPainter>? painter = null, IReadOnlyList<ITreeGutter>? gutters = null)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TreeSurfaceTests).Assembly);
        return session.Dispatch(async () =>
        {
            byte[] bytes = SExpressionTreeFormat.Generate(new Random(7), topLevelChildren);
            var index = new SparseContainerIndex(promotionBytes: 64, checkpointBytes: 16);
            SExpressionTreeFormat.Scan(bytes, new SparseContainerIndexBuilder(index));
            var document = new TreeDocument(index, new SExpressionTreeFormat.Reader(bytes),
                painter?.Invoke(bytes) ?? new SExpressionTreeFormat.Painter(bytes),
                new TreeExpandState(defaultDepth), () => bytes.Length, gutters);

            var rows = new List<TreeRow>();
            var walker = document.NewCursor();
            for (bool more = walker.MoveToStart(); more; more = walker.MoveNext())
                rows.Add(walker.Current);

            var surface = new TreeSurface { Document = document };
            var window = new Window { Width = 700, Height = WindowHeight, Content = surface };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                await body(new Harness(window, surface, bytes, rows, document));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    private static List<(long, bool)> Keys(IEnumerable<TreeRow> rows) => rows.Select(r => r.Key).ToList();

    [Fact]
    public Task RealizesOnlyTheRowsOnScreen_FromTheTop() => WithSurface(defaultDepth: 9, h =>
    {
        Assert.True(h.Rows.Count > 1000, "the document should be far taller than the window");
        Assert.InRange(h.Surface.RealizedRows.Count, FullRows, FullRows + 1);
        Assert.Equal(Keys(h.Rows.Take(h.Surface.RealizedRows.Count)), Keys(h.Surface.RealizedRows));
        return Task.CompletedTask;
    });

    [Fact]
    public Task SmallScrollsMoveByRows() => WithSurface(defaultDepth: 9, async h =>
    {
        h.Surface.ScrollByPixels(3 * RowSurface.RowHeight);
        await PumpAsync();

        Assert.Equal(h.Rows[3].Key, h.Surface.RealizedRows[0].Key);

        h.Surface.ScrollByPixels(RowSurface.RowHeight / 2);
        await PumpAsync();

        Assert.Equal(h.Rows[3].Key, h.Surface.RealizedRows[0].Key);
        Assert.Equal(RowSurface.RowHeight / 2, h.Surface.AnchorPixel, 3);

        h.Surface.ScrollByPixels(-4 * RowSurface.RowHeight);
        await PumpAsync();

        Assert.Equal(h.Rows[0].Key, h.Surface.RealizedRows[0].Key);
        Assert.Equal(0, h.Surface.AnchorPixel);
    });

    [Fact]
    public Task TheWheelScrollsARowPerNotch() => WithSurface(defaultDepth: 9, async h =>
    {
        var centre = h.Surface.TranslatePoint(new Point(200, 100), h.Window)!.Value;
        h.Window.MouseWheel(centre, new Vector(0, -2));
        await PumpAsync();

        Assert.Equal(h.Rows[2].Key, h.Surface.RealizedRows[0].Key);
    });

    [Fact]
    public Task AJumpLandsNearTheSameFractionOfTheFile() => WithSurface(defaultDepth: 9, async h =>
    {
        h.Surface.ScrollToFraction(0.5);
        await PumpAsync();

        long landed = h.Surface.RealizedRows[0].Start;
        Assert.InRange(landed, h.Bytes.Length * 4 / 10, h.Bytes.Length * 6 / 10);
        Assert.InRange(h.Surface.ScrollFraction, 0.4, 0.6);

        // The rows on screen are contiguous rows of the document, whatever the estimate did.
        int first = h.Rows.FindIndex(r => r.Key == h.Surface.RealizedRows[0].Key);
        Assert.Equal(Keys(h.Rows.Skip(first).Take(h.Surface.RealizedRows.Count)), Keys(h.Surface.RealizedRows));
    });

    [Fact]
    public Task DraggingTheThumbMovesSteadilyOneWay() => WithSurface(defaultDepth: 9, async h =>
    {
        // A thumb dragged down in small steps: the top row only ever moves down, and the reported
        // position follows the thumb rather than being pulled back from it.
        long previous = -1;
        for (double fraction = 0.1; fraction < 0.9; fraction += 0.005)
        {
            h.Surface.ScrollToFraction(fraction);
            long top = h.Surface.RealizedRows[0].Start;
            Assert.True(top >= previous, $"at {fraction:0.000} the top went back from {previous} to {top}");
            Assert.InRange(h.Surface.ScrollFraction, fraction - 0.02, fraction + 0.02);
            previous = top;
        }

        await PumpAsync();
    });

    [Fact]
    public Task ScrollingUpSlowlyFromTheEndNeverSnapsBack() => WithSurface(defaultDepth: 9, async h =>
    {
        h.Surface.ScrollToEnd();
        await PumpAsync();
        Assert.True(h.Surface.ShowsEnd);
        Assert.Equal(h.Rows[^1].Key, h.Surface.RealizedRows[^1].Key);

        // A trackpad's worth of small steps up: the top row climbs and never returns to the end.
        int previous = h.Rows.FindIndex(r => r.Key == h.Surface.RealizedRows[0].Key);
        for (int step = 0; step < 200; step++)
        {
            h.Surface.ScrollByPixels(-3);
            int top = h.Rows.FindIndex(r => r.Key == h.Surface.RealizedRows[0].Key);
            Assert.True(top <= previous, $"step {step}: the top row moved from {previous} down to {top}");
            previous = top;
        }

        await PumpAsync();
        Assert.False(h.Surface.ShowsEnd);
        Assert.True(previous < h.Rows.Count - FullRows - 20);
    });

    [Fact]
    public Task KeyboardMovesTheSelectionAndKeepsItOnScreen() => WithSurface(defaultDepth: 9, async h =>
    {
        h.Surface.Focus();
        Press(h.Window, Key.Down); // first key selects the top row
        Assert.Equal(h.Rows[0].Key, h.Surface.SelectedRow!.Value.Key);

        for (int i = 1; i <= FullRows + 5; i++)
        {
            Press(h.Window, Key.Down);
            Assert.Equal(h.Rows[i].Key, h.Surface.SelectedRow!.Value.Key);
        }

        await PumpAsync();
        Assert.Contains(h.Surface.SelectedRow!.Value.Key, Keys(h.Surface.RealizedRows));
        Assert.Equal(h.Surface.SelectedRow!.Value.Key, h.Surface.RealizedRows[FullRows - 1].Key);

        Press(h.Window, Key.End);
        await PumpAsync();
        Assert.Equal(h.Rows[^1].Key, h.Surface.SelectedRow!.Value.Key);
        Assert.Contains(h.Rows[^1].Key, Keys(h.Surface.RealizedRows));

        Press(h.Window, Key.Home);
        await PumpAsync();
        Assert.Equal(h.Rows[0].Key, h.Surface.SelectedRow!.Value.Key);
        Assert.Equal(h.Rows[0].Key, h.Surface.RealizedRows[0].Key);
    });

    [Fact]
    public Task LeftAndRightCollapseExpandAndClimb() => WithSurface(defaultDepth: 9, async h =>
    {
        h.Surface.Focus();
        Press(h.Window, Key.Down); // select the root list
        var root = h.Surface.SelectedRow!.Value;
        Assert.Equal(TreeRowShape.Open, root.Shape);

        Press(h.Window, Key.Left);
        await PumpAsync();
        Assert.False(h.Surface.SelectedRow!.Value.IsExpanded);
        var nextTopLevel = h.Rows.First(r => r.Depth == 0 && r.Node.ValueStart != root.Node.ValueStart);
        Assert.Equal(nextTopLevel.Key, h.Surface.RealizedRows[1].Key); // the next top-level value follows at once

        Press(h.Window, Key.Right);
        Press(h.Window, Key.Right); // into the first child
        await PumpAsync();
        Assert.Equal(h.Rows[1].Key, h.Surface.SelectedRow!.Value.Key);

        Press(h.Window, Key.Left); // back up to the parent
        Assert.Equal(root.Key, h.Surface.SelectedRow!.Value.Key);
    });

    [Fact]
    public Task RevealExpandsWhatHidesTheOffsetAndCentresIt() => WithSurface(defaultDepth: 0, async h =>
    {
        var expanded = new TreeExpandState(99);
        var reference = new TreeCursor(h.Document.Index, h.Document.Reader, expanded);
        reference.SeekTo(h.Bytes.Length / 2);
        var target = reference.Current;

        h.Surface.Reveal(h.Bytes.Length / 2, expandAncestors: true);
        await PumpAsync();

        Assert.Equal(target.Key, h.Surface.SelectedRow!.Value.Key);
        int onScreen = Keys(h.Surface.RealizedRows).IndexOf(target.Key);
        Assert.InRange(onScreen, FullRows / 2 - 2, FullRows / 2 + 2);
    });

    [Fact]
    public Task PagingThroughTheWholeDocumentKeepsCachesToAViewport() => WithSurface(defaultDepth: 9, async h =>
    {
        h.Surface.Focus();
        Press(h.Window, Key.Down);
        for (int page = 0; page < h.Rows.Count / (FullRows - 1) + 2; page++) // a page moves one row short of a screen
        {
            Press(h.Window, Key.PageDown);
            Assert.InRange(h.Surface.CachedLayoutCount, 0, 3 * (FullRows + 1));
        }

        await PumpAsync();
        Assert.Equal(h.Rows[^1].Key, h.Surface.SelectedRow!.Value.Key);
        Assert.Equal(h.Rows[^1].Key, h.Surface.RealizedRows[^1].Key);
    });

    [Fact]
    public Task ClickingTheArrowToggles() => WithSurface(defaultDepth: 9, async h =>
    {
        // The root list's arrow: first row, first indent.
        var arrow = new Point(RowSurface.ContentPaddingX + TreeSurface.ToggleWidth / 2, RowSurface.RowHeight / 2);
        var translated = h.Surface.TranslatePoint(arrow, h.Window)!.Value;

        h.Window.MouseDown(translated, MouseButton.Left);
        h.Window.MouseUp(translated, MouseButton.Left);
        await PumpAsync();

        Assert.False(h.Surface.RealizedRows[0].IsExpanded);
        Assert.Equal(h.Rows[0].Key, h.Surface.SelectedRow!.Value.Key);
    });

    [Fact]
    public Task AssistiveTechnologySeesATreeNamedByItsSelection() => WithSurface(defaultDepth: 9, h =>
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(h.Surface);
        Assert.Equal(AutomationControlType.Tree, peer.GetAutomationControlType());

        h.Surface.Focus();
        Press(h.Window, Key.Down);
        Press(h.Window, Key.Down);

        Assert.Equal(h.Surface.SelectedRowText, peer.GetName());
        Assert.False(string.IsNullOrEmpty(peer.GetName()));
        return Task.CompletedTask;
    });

    /// <summary>Atoms get a link run and lists a marker - enough to drive the link and marker
    /// paths without a real format.</summary>
    private sealed class LinkingPainter(byte[] bytes) : ITreeRowPainter
    {
        private readonly SExpressionTreeFormat.Painter inner = new(bytes);

        public void AppendRuns(in TreeRow row, List<TreeRun> runs)
        {
            inner.AppendRuns(row, runs);
            if (row.Shape == TreeRowShape.Leaf)
                runs.Add(new TreeRun("  [go]", TreeRunStyle.Link, Link: row.Node.ValueStart));
        }

        public string? Marker(in TreeRow row) => row.Shape == TreeRowShape.Leaf ? row.Ordinal.ToString() : null;
    }

    private sealed class FixedGutter : IResizableTreeGutter
    {
        public double Width { get; private set; } = 100;
        public double MinWidth => 40;
        public void Resize(double width) => Width = width;
        public void Draw(Avalonia.Media.DrawingContext context, in TreeRow row, Rect cell, in TreeGutterStyle style) { }
        public object? ToolTipFor(in TreeRow row) => $"tip {row.Node.ValueStart}";
    }

    [Fact]
    public Task ClickingALinkRaisesItInsteadOfToggling() => WithSurface(defaultDepth: 9, async h =>
    {
        int leafIndex = h.Surface.RealizedRows.ToList().FindIndex(r => r.Shape == TreeRowShape.Leaf);
        var leaf = h.Surface.RealizedRows[leafIndex];
        TreeLinkClickedEventArgs? clicked = null;
        h.Surface.LinkClicked += (_, e) => clicked = e;

        var bounds = h.Surface.LinkBounds(leafIndex)!.Value;
        Assert.True(bounds.Width > 0);
        var point = bounds.Center;
        var translated = h.Surface.TranslatePoint(point, h.Window)!.Value;

        h.Window.MouseDown(translated, MouseButton.Left);
        h.Window.MouseUp(translated, MouseButton.Left);
        await PumpAsync();

        Assert.NotNull(clicked);
        Assert.Equal(leaf.Node.ValueStart, clicked!.Link);
        Assert.Equal(leaf.Key, h.Surface.SelectedRow!.Value.Key);
    }, painter: bytes => new LinkingPainter(bytes));

    [Fact]
    public Task AltExpandOpensTheWholeSubtreeAndAltCollapseForgetsIt() => WithSurface(defaultDepth: 1, async h =>
    {
        var root = h.Surface.RealizedRows[0];
        var list = h.Surface.RealizedRows.First(r => r.Shape == TreeRowShape.Open && r.Depth == 1);

        h.Surface.ToggleDeep(list);
        await PumpAsync();

        var everything = new TreeCursor(h.Document.Index, h.Document.Reader, new TreeExpandState(99));
        var shown = new TreeCursor(h.Document.Index, h.Document.Reader, h.Document.Expand);
        everything.SeekTo(list.Start);
        shown.SeekTo(list.Start);
        while (everything.MoveNext() && everything.Current.Depth > 1)
        {
            Assert.True(shown.MoveNext());
            Assert.Equal(everything.Current.Key, shown.Current.Key);
        }

        shown.SeekTo(list.Start);
        h.Surface.ToggleDeep(shown.Current);
        h.Surface.ToggleDeep(shown.Current with { IsExpanded = false }); // open again: only the default beneath
        await PumpAsync();
        Assert.Equal(root.Key, h.Surface.RealizedRows[0].Key);
    });

    [Fact]
    public Task DraggingAResizableGutterEdgeResizesIt() => WithSurface(defaultDepth: 9, async h =>
    {
        var gutter = (FixedGutter)h.Document.Gutters[0];
        double edge = RowSurface.ContentPaddingX + gutter.Width;
        var from = h.Surface.TranslatePoint(new Point(edge, 30), h.Window)!.Value;
        var to = h.Surface.TranslatePoint(new Point(edge + 60, 30), h.Window)!.Value;

        h.Window.MouseDown(from, MouseButton.Left);
        h.Window.MouseMove(to);
        h.Window.MouseUp(to, MouseButton.Left);
        await PumpAsync();

        Assert.Equal(160, gutter.Width, 1);
        Assert.Null(h.Surface.SelectedRow); // a drag on the edge is not a click on a row
    }, gutters: new ITreeGutter[] { new FixedGutter() });
}
