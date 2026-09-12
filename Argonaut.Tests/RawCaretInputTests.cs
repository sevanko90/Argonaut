using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests;

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
[Collection("AppDataPaths")]
public sealed class RawCaretInputTests : IDisposable
{
    private readonly string tempDir;

    public RawCaretInputTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        AppDataPaths.RootOverride = Path.Combine(tempDir, "settings");
    }

    public void Dispose()
    {
        AppDataPaths.RootOverride = null;
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
            var vm = new RawViewModel();
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
            Assert.Equal(vm.Bytes!.Length, vm.Caret.Selection.End);
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
    [Fact]
    public Task TheCaretIsTextHeightAndCentredInItsRow()
        => WithView("abcdef\nghijkl\n", async (window, vm, surface) =>
        {
            Press(window, Key.Right);
            await PumpAsync();

            var rect = surface.CaretRect();
            double? rowTop = surface.CaretRowTop();

            Assert.NotNull(rect);
            Assert.NotNull(rowTop);

            // Shorter than the row, and not by a token amount.
            Assert.InRange(rect!.Value.Height, 8, RawTextSurface.RowHeight - 2);

            // Centred: the gap above equals the gap below.
            double above = rect.Value.Top - rowTop!.Value;
            double below = rowTop.Value + RawTextSurface.RowHeight - rect.Value.Bottom;
            Assert.True(Math.Abs(above - below) <= 1.0,
                $"caret sits {above:F1}px from the top of its row and {below:F1}px from the bottom");
            Assert.True(above > 0, "caret starts at the very top of the row band");
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
            long offset = vm.Bytes!.Length / 2;

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
