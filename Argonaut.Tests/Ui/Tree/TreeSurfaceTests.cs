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
    private static Task WithSurface(int defaultDepth, Func<Harness, Task> body, int topLevelChildren = 200)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TreeSurfaceTests).Assembly);
        return session.Dispatch(async () =>
        {
            byte[] bytes = SExpressionTreeFormat.Generate(new Random(7), topLevelChildren);
            var index = new SparseContainerIndex(promotionBytes: 64, checkpointBytes: 16);
            SExpressionTreeFormat.Scan(bytes, new SparseContainerIndexBuilder(index));
            var document = new TreeDocument(index, new SExpressionTreeFormat.Reader(bytes), new SExpressionTreeFormat.Painter(bytes),
                new TreeExpandState(defaultDepth), () => bytes.Length);

            var rows = new List<TreeRow>();
            var walker = document.NewCursor();
            for (bool more = walker.MoveToStart(); more; more = walker.MoveNext())
                rows.Add(walker.Current);

            var surface = new TreeSurface { Document = document };
            var window = new Window { Width = 700, Height = WindowHeight, Content = new ScrollViewer { Content = surface } };
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
        h.Surface.Offset = new Vector(0, h.Surface.Offset.Y + 3 * RowSurface.RowHeight);
        await PumpAsync();

        Assert.Equal(h.Rows[3].Key, h.Surface.RealizedRows[0].Key);

        h.Surface.Offset = new Vector(0, h.Surface.Offset.Y + RowSurface.RowHeight / 2);
        await PumpAsync();

        Assert.Equal(h.Rows[3].Key, h.Surface.RealizedRows[0].Key);
        Assert.Equal(RowSurface.RowHeight / 2, h.Surface.AnchorPixel, 3);

        h.Surface.Offset = new Vector(0, h.Surface.Offset.Y - 4 * RowSurface.RowHeight);
        await PumpAsync();

        Assert.Equal(h.Rows[0].Key, h.Surface.RealizedRows[0].Key);
    });

    [Fact]
    public Task AJumpLandsNearTheSameFractionOfTheFile() => WithSurface(defaultDepth: 9, async h =>
    {
        double half = (h.Surface.Extent.Height - h.Surface.Viewport.Height) / 2;
        h.Surface.Offset = new Vector(0, half);
        await PumpAsync();

        long landed = h.Surface.RealizedRows[0].Start;
        Assert.InRange(landed, h.Bytes.Length * 3 / 10, h.Bytes.Length * 7 / 10);

        // The rows on screen are contiguous rows of the document, whatever the estimate did.
        int first = h.Rows.FindIndex(r => r.Key == h.Surface.RealizedRows[0].Key);
        Assert.Equal(Keys(h.Rows.Skip(first).Take(h.Surface.RealizedRows.Count)), Keys(h.Surface.RealizedRows));
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
}
