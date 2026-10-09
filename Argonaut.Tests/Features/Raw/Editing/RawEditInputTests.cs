using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw;
using Argonaut.Features.Raw.Editing;
using Argonaut.Features.Raw.Rows;
using Argonaut.Tests.Support;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Raw.Editing;

/// <summary>
/// Real keystrokes at a real window, proving edit mode is wired to the keyboard - the rules
/// themselves are <see cref="RawEditControllerTests"/>'s job, with no UI.
///
/// This suite exists because of the sharpest defect the caret work produced: every input test
/// called <c>Focus()</c> in its own setup, so the whole suite passed against an application in
/// which no key did anything. Nothing here focuses the surface by hand - the view is expected to
/// do it when edit mode turns on, which is the wiring most likely to be wrong.
///
/// The two harness rules in <see cref="RawCaretInputTests"/> apply here too and are not optional
/// (see docs/headless-test-dispatch-hole.md): the dispatch body ends with <c>return true;</c> and
/// the test returns the dispatch task rather than awaiting it.
/// </summary>
public sealed class RawEditInputTests : IDisposable
{
    private readonly string tempDir;

    public RawEditInputTests()
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

    private static void TypeText(Window window, string text) => window.KeyTextInput(text);

    private const string FocusSinkName = "FocusSink";

    private static Button FocusSink(Window window)
        => window.GetVisualDescendants().OfType<Button>().First(b => b.Name == FocusSinkName);

    /// <summary>What the document now reads as - the piece table, not the file on disk.</summary>
    private static string DocumentText(RawViewModel vm)
    {
        var source = vm.Document!;
        var bytes = new byte[source.AvailableLength];
        source.CopyTo(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a loaded, laid-out raw view whose indexing has
    /// finished, with edit mode already on. Turning it on through the view model is what the
    /// toolbar toggle does, so the focus hand-off this exercises is the real one.
    /// </summary>
    private Task WhileEditing(string content, Func<Window, RawViewModel, RawTextSurface, Task> body)
        => WithView(content, async (window, vm, surface) =>
        {
            vm.SetEditing(true);
            await PumpAsync();
            Assert.True(vm.IsEditing);

            await body(window, vm, surface);
        });

    private Task WithView(string content, Func<Window, RawViewModel, RawTextSurface, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawEditInputTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel(new RawViewSettings());
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteFile(content));
                await vm.IndexingTask;

                var view = new RawView { DataContext = vm };

                // A zero-sized focusable sibling, so a test can park focus somewhere other than
                // the surface without changing the viewport the rows are laid out into.
                var focusSink = new Button { Name = FocusSinkName, Width = 0, Height = 0 };
                window = new Window
                {
                    Width = 800,
                    Height = 400,
                    Content = new Grid { Children = { view, focusSink } }
                };
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
    public Task TypingAtTheCaret_ChangesTheDocument()
        => WhileEditing("hello world\n", async (window, vm, _) =>
        {
            vm.Caret!.PlaceAt(5);
            await PumpAsync();

            TypeText(window, ",");
            await PumpAsync();

            Assert.Equal("hello, world\n", DocumentText(vm));
            Assert.Equal(6, vm.Caret.Caret.Offset);
            Assert.True(vm.IsDirty);
        });

    [Fact]
    public Task TypingWhileNotInEditMode_LeavesTheDocumentAlone()
        => WithView("hello world\n", async (window, vm, _) =>
        {
            TypeText(window, "X");
            await PumpAsync();

            Assert.False(vm.IsDirty);
            Assert.Equal("hello world\n", DocumentText(vm));
        });

    [Fact]
    public Task Backspace_RemovesTheCharacterBeforeTheCaret()
        => WhileEditing("abc\n", async (window, vm, _) =>
        {
            vm.Caret!.PlaceAt(2);
            await PumpAsync();

            Press(window, Key.Back);
            await PumpAsync();

            Assert.Equal("ac\n", DocumentText(vm));
            Assert.Equal(1, vm.Caret.Caret.Offset);
        });

    [Fact]
    public Task Delete_RemovesTheCharacterUnderTheCaret()
        => WhileEditing("abc\n", async (window, vm, _) =>
        {
            vm.Caret!.PlaceAt(1);
            await PumpAsync();

            Press(window, Key.Delete);
            await PumpAsync();

            Assert.Equal("ac\n", DocumentText(vm));
        });

    [Fact]
    public Task Enter_SplitsTheLineAndTheViewGainsARow()
        => WhileEditing("abcdef\n", async (window, vm, _) =>
        {
            int rowsBefore = vm.RowCount;
            vm.Caret!.PlaceAt(3);
            await PumpAsync();

            Press(window, Key.Enter);
            await PumpAsync();

            Assert.Equal("abc\ndef\n", DocumentText(vm));
            Assert.Equal(rowsBefore + 1, vm.RowCount);
        });

    [Fact]
    public Task TypedText_ShowsUpInTheRowTheViewDraws()
        => WhileEditing("abc\ndef\n", async (window, vm, surface) =>
        {
            vm.Caret!.PlaceAt(0);
            await PumpAsync();

            TypeText(window, "Z");
            await PumpAsync();
            window.UpdateLayout();
            await PumpAsync();

            // Through the row collection the surface reads, not through the piece table: a view
            // that keeps drawing cached rows after an edit is the failure worth catching here.
            Assert.Equal("Zabc", ((RawVisibleRow)vm.Rows[0]!).Text);
            Assert.True(surface.RealizedRowCount >= 2);
        });

    [Fact]
    public Task UndoAndRedo_WalkTheDocumentBackAndForward()
        => WhileEditing("abc\n", async (window, vm, _) =>
        {
            vm.Caret!.PlaceAt(3);
            await PumpAsync();

            TypeText(window, "d");
            await PumpAsync();
            Assert.Equal("abcd\n", DocumentText(vm));

            Press(window, Key.Z, RawInputModifiers.Control);
            await PumpAsync();
            Assert.Equal("abc\n", DocumentText(vm));

            Press(window, Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
            await PumpAsync();
            Assert.Equal("abcd\n", DocumentText(vm));
        });

    [Fact]
    public Task TypingOverASelection_ReplacesIt()
        => WhileEditing("hello world\n", async (window, vm, _) =>
        {
            vm.Caret!.PlaceAt(6);
            vm.Caret.ExtendTo(11);
            await PumpAsync();

            TypeText(window, "there");
            await PumpAsync();

            Assert.Equal("hello there\n", DocumentText(vm));
        });

    [Fact]
    public Task EditModeRefusesToStart_UntilIndexingHasFinished()
        => WithView("abc\n", async (_, vm, _) =>
        {
            // The document here is already indexed, so this is the positive half; the refusal
            // itself is RawEditControllerTests' Constructing_OverAnUnfinishedScan_Throws.
            Assert.True(vm.CanEdit);

            vm.SetEditing(true);
            await PumpAsync();
            Assert.True(vm.IsEditing);
        });

    [Fact]
    public Task LeavingEditModeWithoutTyping_PutsTheViewBackOnTheFile()
        => WhileEditing("abc\n", async (_, vm, _) =>
        {
            vm.SetEditing(false);
            await PumpAsync();

            Assert.False(vm.IsEditing);
            Assert.False(vm.IsDirty);

            // No piece table left behind, so re-wrapping is available again.
            Assert.Same(vm.Bytes, vm.Document);
        });

    [Fact]
    public Task LeavingEditModeAfterTyping_KeepsTheEdits()
        => WhileEditing("abc\n", async (window, vm, _) =>
        {
            vm.Caret!.PlaceAt(3);
            await PumpAsync();
            TypeText(window, "d");
            await PumpAsync();

            vm.SetEditing(false);
            await PumpAsync();

            Assert.False(vm.IsEditing);
            Assert.True(vm.IsDirty);
            Assert.Equal("abcd\n", DocumentText(vm));
        });

    [Fact]
    public Task TurningEditModeOn_TakesFocusBackFromTheToolbar()
        => WithView("abc\n", async (window, vm, surface) =>
        {
            // The toggle lives in the header toolbar, so the click that turns editing on leaves
            // focus there. Standing in for it with any other focusable control is enough to show
            // the view puts focus back - without it the first thing typed goes nowhere.
            FocusSink(window).Focus();
            await PumpAsync();
            Assert.False(surface.IsFocused);

            vm.SetEditing(true);
            await PumpAsync();

            TypeText(window, "Z");
            await PumpAsync();

            Assert.Equal("Zabc\n", DocumentText(vm));
        });

    // ---- re-wrapping while edited -----------------------------------------------------------
    //
    // A wrap change while edited scans the file's original bytes at the new width on the
    // background and moves the edits onto that scan when it finishes; until then the view stays at
    // the old width and editing carries on. RawEditedRowIndexTests holds the rows themselves to a
    // fresh index; these prove the view model swaps everything that reads them.

    private static readonly string LongLines = string.Concat(Enumerable.Repeat(new string('w', 300) + "\n", 20));

    /// <summary>Pumps until <paramref name="done"/> holds, so a test waits on the background scan
    /// without assuming how long it takes.</summary>
    private static async Task PumpUntilAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await PumpAsync(10);
        }
    }

    /// <summary>Rows a fresh scan of what the document now reads as would have.</summary>
    private static int FreshRowCount(RawViewModel vm, int width)
    {
        var bytes = Encoding.UTF8.GetBytes(DocumentText(vm));
        var scan = RawSegmentIndex.StartIndexing(new MemoryByteSource(bytes), width);
        scan.IndexingTask.GetAwaiter().GetResult();
        return scan.RowCount;
    }

    [Fact]
    public Task WrapWidth_StaysChangeableWhileEditing()
        => WhileEditing("abc\n", async (_, vm, _) =>
        {
            await PumpAsync();
            Assert.True(((RawToolbarViewModel)vm.Toolbar!).CanChangeWrapWidth);
        });

    [Fact]
    public Task WrapWidth_ChangesWhileEdited_AndTheEditsSurvive()
        => WhileEditing(LongLines, async (window, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 160 : 80;
            vm.Caret!.PlaceAt(1_000);
            await PumpAsync();
            TypeText(window, "Q");
            await PumpAsync();
            string edited = DocumentText(vm);
            int generation = vm.IndexGeneration;

            vm.SetWrapWidth(target);
            await PumpUntilAsync(() => vm.WrapWidth == target, "the re-wrap");

            Assert.True(vm.IsEditing);
            Assert.True(vm.IsDirty);
            Assert.Equal(edited, DocumentText(vm));
            Assert.Equal(1_001, vm.Caret!.Caret.Offset);
            Assert.Equal(FreshRowCount(vm, target), vm.RowCount);
            Assert.Equal(vm.RowCount, vm.Rows.Count);
            Assert.True(vm.IndexGeneration > generation);
        });

    [Fact]
    public Task TypingAfterARewrap_EditsTheDocumentAtTheNewWidth()
        => WhileEditing(LongLines, async (window, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 120 : 80;
            vm.Caret!.PlaceAt(10);
            await PumpAsync();
            TypeText(window, "a");
            await PumpAsync();

            vm.SetWrapWidth(target);
            await PumpUntilAsync(() => vm.WrapWidth == target, "the re-wrap");

            // Typing must reach the editor that now owns the rows, at the caret it carried across.
            TypeText(window, "b");
            await PumpAsync();
            Press(window, Key.Enter);
            await PumpAsync();

            Assert.StartsWith("wwwwwwwwwwab\n", DocumentText(vm));
            Assert.Equal(FreshRowCount(vm, target), vm.RowCount);
        });

    [Fact]
    public Task UndoAfterARewrap_WalksBackEditsMadeBeforeIt()
        => WhileEditing(LongLines, async (window, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 40 : 80;
            vm.Caret!.PlaceAt(500);
            await PumpAsync();
            TypeText(window, "Z");
            await PumpAsync();

            vm.SetWrapWidth(target);
            await PumpUntilAsync(() => vm.WrapWidth == target, "the re-wrap");

            Press(window, Key.Z, RawInputModifiers.Control);
            await PumpAsync();

            Assert.Equal(LongLines, DocumentText(vm));
            Assert.False(vm.IsDirty);
            Assert.Equal(FreshRowCount(vm, target), vm.RowCount);
        });

    [Fact]
    public Task TwoQuickWrapChanges_EndAtTheLastOne()
        => WhileEditing(LongLines, async (window, vm, _) =>
        {
            int first = vm.WrapWidth == 80 ? 40 : 80;
            int last = 120;
            vm.Caret!.PlaceAt(700);
            await PumpAsync();
            TypeText(window, "M");
            await PumpAsync();

            vm.SetWrapWidth(first);
            vm.SetWrapWidth(last);
            await PumpUntilAsync(() => vm.WrapWidth == last, "the second re-wrap");

            // Long enough for a stale first scan to land if it was going to.
            await PumpAsync(100);

            Assert.Equal(last, vm.WrapWidth);
            Assert.Equal(FreshRowCount(vm, last), vm.RowCount);
        });

    [Fact]
    public Task LeavingEditModeUntouchedAfterARewrap_KeepsTheNewWidth()
        => WhileEditing(LongLines, async (_, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 50 : 80;

            vm.SetWrapWidth(target);
            await PumpUntilAsync(() => vm.WrapWidth == target, "the re-wrap");

            vm.SetEditing(false);
            await PumpAsync();

            // Back on the file, whose index must be the one at the new width.
            Assert.Same(vm.Bytes, vm.Document);
            Assert.Equal(target, vm.WrapWidth);
            Assert.Equal(FreshRowCount(vm, target), vm.RowCount);
        });

    [Fact]
    public Task EnteringEditModeAgainAfterARewrap_EditsAtTheNewWidth()
        => WithView(LongLines, async (window, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 64 : 80;
            vm.SetEditing(true);
            await PumpAsync();
            vm.SetWrapWidth(target);
            await PumpUntilAsync(() => vm.WrapWidth == target, "the re-wrap");
            vm.SetEditing(false);
            await PumpAsync();

            vm.SetEditing(true);
            await PumpAsync();
            vm.Caret!.PlaceAt(3);
            await PumpAsync();
            Press(window, Key.Enter);
            await PumpAsync();

            Assert.True(vm.IsDirty);
            Assert.Equal(FreshRowCount(vm, target), vm.RowCount);
        });

    // ---- position across a re-wrap ----------------------------------------------------------
    //
    // A wrap change replaces every row, so the old scroll offset means nothing - but the user's
    // place in the document does. A caret on screen stays on screen at the same height; a caret
    // the user has scrolled away from leaves the top of the view where it was, by byte offset.
    // Both an edited and an unedited document keep it.

    /// <summary>Lines several rows long at every width the tests use, enough of them that the
    /// middle of the document is far from either end.</summary>
    private static readonly string TallDocument = string.Concat(Enumerable.Repeat(new string('w', 300) + "\n", 2_000));

    private const long DeepOffset = 1_000 * 301 + 150;

    private static int CaretRow(RawViewModel vm) => vm.RowIndex!.RowForOffset(vm.Caret!.Caret.Offset)!.Value;

    private static (int First, int Last) Viewport(RawViewModel vm)
        => vm.ViewportRows ?? throw new InvalidOperationException("the surface has not reported a viewport");

    /// <summary>Waits for the re-wrap to land and the surface to lay out over its rows.</summary>
    private static async Task RewrapAndSettleAsync(RawViewModel vm, int target)
    {
        vm.SetWrapWidth(target);
        await PumpUntilAsync(() => vm.WrapWidth == target, "the re-wrap");
        await PumpUntilAsync(() => vm.IndexingTask.IsCompleted, "the re-wrap's scan");
        await PumpAsync(50);
        await PumpAsync(50);
    }

    [Fact]
    public Task RewrapWhileEdited_KeepsTheCaretWhereItWasOnScreen()
        => WhileEditing(TallDocument, async (window, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 120 : 80;
            await vm.JumpToByteOffsetAsync(DeepOffset);
            await PumpAsync();
            TypeText(window, "Q");
            await PumpAsync();

            long caret = vm.Caret!.Caret.Offset;
            var before = Viewport(vm);
            int heightBefore = CaretRow(vm) - before.First;
            Assert.InRange(CaretRow(vm), before.First, before.Last);

            await RewrapAndSettleAsync(vm, target);

            Assert.Equal(caret, vm.Caret!.Caret.Offset);
            var after = Viewport(vm);
            Assert.InRange(CaretRow(vm), after.First, after.Last);
            Assert.Equal(heightBefore, CaretRow(vm) - after.First);
        });

    [Fact]
    public Task RewrapWhileEdited_WithTheCaretScrolledAway_KeepsTheTopOfTheView()
        => WhileEditing(TallDocument, async (window, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 120 : 80;
            vm.Caret!.PlaceAt(0);
            await PumpAsync();
            TypeText(window, "Q");
            await PumpAsync();

            // Scroll deep into the document without moving the caret, as the scrollbar would.
            vm.SelectRow(vm.RowIndex!.RowForOffset(DeepOffset)!.Value);
            await PumpAsync();
            await PumpAsync();
            var before = Viewport(vm);
            Assert.True(CaretRow(vm) < before.First, "the caret should be off screen above the view");
            long topStart = vm.RowIndex!.GetRowInfo(before.First).Start;

            await RewrapAndSettleAsync(vm, target);

            Assert.Equal(1, vm.Caret!.Caret.Offset);
            Assert.Equal(vm.RowIndex!.RowForOffset(topStart), Viewport(vm).First);
        });

    [Fact]
    public Task RewrapUnedited_KeepsTheCaretWhereItWasOnScreen()
        => WithView(TallDocument, async (_, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 120 : 80;
            await vm.JumpToByteOffsetAsync(DeepOffset);
            await PumpAsync();
            await PumpAsync();

            var before = Viewport(vm);
            int heightBefore = CaretRow(vm) - before.First;
            Assert.InRange(CaretRow(vm), before.First, before.Last);

            await RewrapAndSettleAsync(vm, target);

            Assert.Equal(DeepOffset, vm.Caret!.Caret.Offset);
            var after = Viewport(vm);
            Assert.InRange(CaretRow(vm), after.First, after.Last);
            Assert.Equal(heightBefore, CaretRow(vm) - after.First);
        });

    [Fact]
    public Task RewrapUnedited_WithTheCaretScrolledAway_KeepsTheTopOfTheView()
        => WithView(TallDocument, async (_, vm, _) =>
        {
            int target = vm.WrapWidth == 80 ? 120 : 80;
            vm.SelectRow(vm.RowIndex!.RowForOffset(DeepOffset)!.Value);
            await PumpAsync();
            await PumpAsync();
            var before = Viewport(vm);
            Assert.True(CaretRow(vm) < before.First, "the caret should be off screen above the view");
            long topStart = vm.RowIndex!.GetRowInfo(before.First).Start;

            await RewrapAndSettleAsync(vm, target);

            Assert.Equal(vm.RowIndex!.RowForOffset(topStart), Viewport(vm).First);
        });

    // ---- edit overview --------------------------------------------------------------------

    private static string ManyLines(int count)
    {
        var text = new StringBuilder();
        for (int i = 0; i < count; i++)
            text.Append($"line {i:D4}\n");

        return text.ToString();
    }

    [Fact]
    public Task EditOverview_IsHiddenUntilThereIsAnEditor()
        => WithView(ManyLines(10), async (window, vm, _) =>
        {
            var overview = window.GetVisualDescendants().OfType<RawEditOverview>().Single();
            Assert.False(overview.IsVisible);

            vm.SetEditing(true);
            await PumpAsync();

            Assert.True(overview.IsVisible);
            Assert.Empty(overview.Marks);
        });

    [Fact]
    public Task EditOverview_MarksAnEditAndClickingItPutsTheCaretThere()
        => WhileEditing(ManyLines(2000), async (window, vm, _) =>
        {
            var overview = window.GetVisualDescendants().OfType<RawEditOverview>().Single();
            long editAt = vm.RowIndex!.GetRowInfo(1500).Start + 2;
            vm.Caret!.PlaceAt(editAt);
            await PumpAsync();
            TypeText(window, "Q");
            await PumpAsync();

            var mark = Assert.Single(overview.Marks);
            Assert.Equal(editAt, mark.Offset);
            Assert.InRange(mark.Top, overview.Bounds.Height * 0.7, overview.Bounds.Height * 0.8);

            // Somewhere else entirely, then back via the mark.
            vm.Caret.PlaceAt(0);
            await PumpAsync();
            var point = overview.TranslatePoint(new Avalonia.Point(overview.Bounds.Width / 2, mark.Top + 1), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await PumpAsync();

            Assert.Equal(editAt, vm.Caret.Caret.Offset);
        });
}
