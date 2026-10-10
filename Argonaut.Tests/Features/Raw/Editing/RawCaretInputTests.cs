using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw;
using Argonaut.Tests.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Raw.Editing;

/// <summary>
/// Real keyboard and mouse input driven at a real window, to prove the caret is actually wired to
/// the events rather than merely correct in the abstract - <see cref="RawCaretControllerTests"/>
/// already covers the movement rules themselves with no UI.
///
/// Two harness rules apply here and are not optional (see docs/headless-test-dispatch-hole.md):
/// the dispatch body ends with <c>return true;</c> and the test returns the dispatch task rather
/// than awaiting it, because an <c>async</c> body with no return value binds to the wrong
/// overload and every assertion after the first await runs unobserved - the test passes whatever
/// happens. Each test here was confirmed to go red with an inverted assertion.
/// </summary>
public sealed class RawCaretInputTests : IDisposable
{
    private readonly string tempDir;

    public RawCaretInputTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(string content)
    {
        string path = Path.Combine(tempDir, "doc.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static async Task PumpAsync(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        => window.KeyPress(key, modifiers, PhysicalKey.None, string.Empty);

    /// <summary>
    /// Runs <paramref name="body"/> against a loaded, laid-out raw view. Everything the tests
    /// need is here so each one is about the input it sends, not the scaffolding.
    /// </summary>
    private Task WithView(string content, Func<Window, RawViewModel, RawTextSurface, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawCaretInputTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel(new RawViewSettings());
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteFile(content));
                await vm.IndexingTask;

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 800, Height = 400, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var surface = window.GetVisualDescendants().OfType<RawTextSurface>().First();
                await PumpAsync();

                await body(window, vm, surface);
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task ArrowKeys_MoveTheCaret()
        => WithView("abcdef\nghijkl\n", async (window, vm, _) =>
        {
            Assert.Equal(0, vm.Caret!.Caret.Offset);

            Press(window, Key.Right);
            await PumpAsync();
            Assert.Equal(1, vm.Caret.Caret.Offset);

            Press(window, Key.Right);
            await PumpAsync();
            Assert.Equal(2, vm.Caret.Caret.Offset);

            Press(window, Key.Left);
            await PumpAsync();
            Assert.Equal(1, vm.Caret.Caret.Offset);
        });

    [Fact]
    public Task ShiftArrow_ExtendsTheSelection()
        => WithView("abcdef\n", async (window, vm, _) =>
        {
            Press(window, Key.Right, RawInputModifiers.Shift);
            Press(window, Key.Right, RawInputModifiers.Shift);
            await PumpAsync();

            Assert.Equal(0, vm.Caret!.Selection.Start);
            Assert.Equal(2, vm.Caret.Selection.End);

            // Moving without shift collapses rather than extending.
            Press(window, Key.Right);
            await PumpAsync();
            Assert.True(vm.Caret.Selection.IsEmpty);
            Assert.Equal(2, vm.Caret.Caret.Offset);
        });

    [Fact]
    public Task HomeAndEnd_BoundTheRow()
        => WithView("abcdef\nghijkl\n", async (window, vm, _) =>
        {
            Press(window, Key.Right);
            Press(window, Key.Right);
            Press(window, Key.End);
            await PumpAsync();
            Assert.Equal(6, vm.Caret!.Caret.Offset); // before the newline

            Press(window, Key.Home);
            await PumpAsync();
            Assert.Equal(0, vm.Caret.Caret.Offset);
        });

    [Fact]
    public Task DownArrow_MovesToTheNextRowKeepingTheColumn()
        => WithView("abcdef\nghijkl\n", async (window, vm, _) =>
        {
            Press(window, Key.Right);
            Press(window, Key.Right);
            Press(window, Key.Right);
            await PumpAsync();
            Assert.Equal(3, vm.Caret!.Caret.Offset);

            Press(window, Key.Down);
            await PumpAsync();

            // Row 1 starts at byte 7 ("abcdef\n"), so the same column is 7 + 3.
            Assert.Equal(10, vm.Caret.Caret.Offset);

            Press(window, Key.Up);
            await PumpAsync();
            Assert.Equal(3, vm.Caret.Caret.Offset);
        });

    [Fact]
    public Task SelectAll_CoversTheDocument()
        => WithView("abcdef\nghijkl\n", async (window, vm, _) =>
        {
            Press(window, Key.A, RawInputModifiers.Control);
            await PumpAsync();

            Assert.Equal(0, vm.Caret!.Selection.Start);
            Assert.Equal(vm.Bytes!.AvailableLength, vm.Caret.Selection.End);
        });

    [Fact]
    public Task ClickingPlacesTheCaretAndDraggingSelects()
        => WithView("abcdef\nghijkl\n", async (window, vm, surface) =>
        {
            // Several characters along the first row, past the line-number gutter.
            double textLeft = RawTextSurface.ContentPaddingX + RawTextSurface.LineNumberColumnWidth;
            var start = new Point(textLeft + 25, RawTextSurface.RowHeight / 2);

            window.MouseDown(start, MouseButton.Left);
            await PumpAsync();

            long placed = vm.Caret!.Caret.Offset;
            Assert.True(placed > 0, "clicking into the middle of a row left the caret at offset 0");
            Assert.InRange(placed, 1, 6);
            Assert.True(vm.Caret.Selection.IsEmpty);

            // Drag onto the second row: the selection must span rows.
            var end = new Point(textLeft + 30, RawTextSurface.RowHeight * 1.5);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            await PumpAsync();

            Assert.False(vm.Caret.Selection.IsEmpty);
            Assert.True(vm.Caret.Selection.End > 7,
                $"drag to the second row selected only up to {vm.Caret.Selection.End}");

            window.MouseUp(end, MouseButton.Left);
            await PumpAsync();
        });

    [Fact]
    public Task TheCaretSurvivesAWrapWidthChange()
        => WithView(new string('x', 400) + "\n", async (window, vm, _) =>
        {
            Press(window, Key.Right);
            Press(window, Key.Right);
            Press(window, Key.Right);
            await PumpAsync();
            long before = vm.Caret!.Caret.Offset;
            Assert.Equal(3, before);

            // Re-wrapping renumbers every row; a caret is a byte offset, so it does not move.
            vm.SetWrapWidth(80);
            await vm.IndexingTask;
            await PumpAsync();

            Assert.Equal(before, vm.Caret!.Caret.Offset);
        });

    [Fact]
    public Task TheCaretNeverLandsInsideAMultiByteCharacter()
        => WithView("aébc\n", async (window, vm, _) =>
        {
            // 'é' occupies bytes 1 and 2, so arrowing right must step 0, 1, 3, 4...
            var visited = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                visited.Add(vm.Caret!.Caret.Offset);
                Press(window, Key.Right);
                await PumpAsync();
            }

            Assert.Equal(new long[] { 0, 1, 3, 4 }, visited);
        });
    /// <summary>
    /// The caret must be as tall as the text and sit centred in its row band, not span the whole
    /// 22px row - which overhangs the glyphs by the row's leading and reads as a caret that is
    /// too long and hangs below the line.
    /// </summary>
    /// <summary>A jump in flashes where it landed: starting at the caret, on the caret's row,
    /// over the text after it - and only until the flash is over.</summary>
    [Fact]
    public Task AnArrivalFlashesAtTheCaret()
        => WithView("abcdef\nghijkl mnopqr stuvwx\n", async (window, vm, surface) =>
        {
            Assert.Null(surface.ArrivalRect());

            await vm.RevealByteRangeAsync(ByteRange.At(9));
            await PumpAsync();

            var flash = surface.ArrivalRect();
            var caret = surface.CaretRect();
            Assert.NotNull(flash);
            Assert.NotNull(caret);
            Assert.True(Math.Abs(flash!.Value.Left - caret!.Value.Left) <= 3, $"flash starts at {flash.Value.Left}, caret at {caret.Value.Left}");
            Assert.True(flash.Value.Width > caret.Value.Width * 4, "the flash covers the text after the caret, not just the caret");
            Assert.True(flash.Value.Top < caret.Value.Bottom && flash.Value.Bottom > caret.Value.Top, "the flash is on the caret's row");

            surface.EndArrivalFlash();
            Assert.Null(surface.ArrivalRect());
        });

    /// <summary>
    /// With the view panned right from earlier, a landing near the end of a short line whose
    /// flash runs on into the next line pans back so the part at that line's start shows too -
    /// the caret alone, being on screen already, would not move the view.
    /// </summary>
    [Fact]
    public Task AnArrivalPansToShowTheWholeFlash()
        => WithView(new string('b', 30) + "\n" + new string('c', 50) + "\n" + new string('a', 200) + "\n", async (window, vm, surface) =>
        {
            // Panned a little right, as a long row elsewhere allows: the start of each line is
            // just off the left, while the end of the b line is well on screen.
            var panBar = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Horizontal);
            Assert.True(panBar.IsVisible, "the long row should make the view pannable");
            panBar.Value = 20;
            await PumpAsync();
            Assert.Equal(20, surface.PanOffset, 1);

            // Four b's from the end: the flash runs over them, the newline, then the c line's start.
            await vm.RevealByteRangeAsync(ByteRange.At(26));
            await PumpAsync(60);

            var landing = surface.ArrivalRect();
            var rest = surface.ArrivalRect(rowsAfter: 1);
            Assert.NotNull(landing);
            Assert.NotNull(rest);
            foreach (var rect in new[] { landing!.Value, rest!.Value })
            {
                Assert.True(rect.Left >= surface.TextViewportLeft - 0.5, $"flash starts at {rect.Left}, left of {surface.TextViewportLeft}");
                Assert.True(rect.Right <= surface.TextViewportRight + 0.5, $"flash ends at {rect.Right}, past {surface.TextViewportRight}");
            }
        });

    /// <summary>Landing at the end of a wrapped row, the flash runs on at the start of the next -
    /// where the text it points at actually continues.</summary>
    [Fact]
    public Task AnArrivalAtTheEndOfARowFlashesOnIntoTheNext()
        => WithView(new string('a', 200) + "\n", async (window, vm, surface) =>
        {
            long wrap = vm.RowIndex!.GetRowInfo(0).End;
            await vm.RevealByteRangeAsync(ByteRange.At(wrap - 4));
            await PumpAsync();

            var landing = surface.ArrivalRect();
            var next = surface.ArrivalRect(rowsAfter: 1);
            Assert.NotNull(landing);
            Assert.NotNull(next);
            Assert.True(next!.Value.Left < landing!.Value.Left, "the rest starts back at the row's start");
            Assert.True(next.Value.Width > landing.Value.Width, "most of the flash is on the next row");
            Assert.Null(surface.ArrivalRect(rowsAfter: 2));
        });

    /// <summary>Landing near the right edge of a row wider than the window pans just enough to
    /// show what the flash points at, without losing the landing point off the left.</summary>
    [Fact]
    public Task AnArrivalAtTheRightEdgePansToShowTheFlash()
        => WithView(new string('a', 150) + "\n", async (window, vm, surface) =>
        {
            // Every landing along a row wider than the window, the right-hand ones included.
            for (long offset = 60; offset <= 140; offset += 10)
            {
                await vm.RevealByteRangeAsync(ByteRange.At(offset));
                await PumpAsync(60);

                var shown = surface.ArrivalRect();
                Assert.NotNull(shown);
                Assert.True(shown!.Value.Right <= surface.TextViewportRight + 0.5,
                    $"landing at {offset}: flash ends at {shown.Value.Right}, past {surface.TextViewportRight}");
                Assert.True(shown.Value.Left > 0, $"landing at {offset} went off the left");
            }
        });

    /// <summary>
    /// The whole jump path, as the failure-location link drives it: resolve a byte offset, reveal
    /// it, place the caret. Exercised end to end rather than by calling the surface's reveal
    /// directly, because the ordering between placing the caret and revealing the row is itself
    /// capable of defeating the centring - the caret's own scroll-into-view is a minimal scroll,
    /// and if it runs first it parks the row at the bottom edge, after which the reveal finds it
    /// "already visible" and leaves it there.
    /// </summary>
    [Fact]
    public Task JumpingToAnOffsetCentresItsRow()
    {
        var content = new StringBuilder();
        for (int i = 0; i < 400; i++)
            content.Append($"line {i:D4} of the document\n");

        return WithView(content.ToString(), async (window, vm, surface) =>
        {
            // A byte offset well past the first screenful.
            long offset = vm.Bytes!.AvailableLength / 2;

            await vm.JumpToByteOffsetAsync(offset);
            await PumpAsync();
            window.UpdateLayout();

            int target = vm.SelectedRowIndex!.Value;
            var range = surface.RealizedRowRange;

            Assert.InRange(target, range.First, range.Last);

            int above = target - range.First;
            int below = range.Last - target;
            Assert.True(above > 3,
                $"target row {target} sits {above} rows from the top and {below} from the bottom - not centred");
            Assert.True(Math.Abs(above - below) <= 2,
                $"target row {target} sits {above} rows from the top and {below} from the bottom");
        });
    }
    /// <summary>
    /// The surface must take focus when it is shown. Without it the caret is invisible - it is
    /// hidden while unfocused - and arrow keys never reach the editor at all: unhandled, they
    /// fall through to Avalonia's directional navigation and walk focus off to the find bar.
    ///
    /// Every other test in this file used to call Focus() itself, which is exactly why none of
    /// them noticed.
    /// </summary>
    [Fact]
    public Task TheSurfaceTakesFocusWhenShown()
        => WithView("abcdef\nghijkl\n", async (window, vm, surface) =>
        {
            Assert.True(surface.IsFocused, "the raw surface does not have focus when the view is shown");

            // And so the arrows reach it without anything focusing it by hand.
            Press(window, Key.Right);
            await PumpAsync();
            Assert.Equal(1, vm.Caret!.Caret.Offset);
        });
}
