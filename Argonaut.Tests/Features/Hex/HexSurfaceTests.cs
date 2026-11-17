using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex;
using Argonaut.Ui.Rows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace Argonaut.Tests.Features.Hex;

/// <summary>
/// The hex surface over documents of any size: rows read only as they come on screen, the scroll
/// model exact even at billions of rows, and rows first reached while painting laid out inside
/// the frame without a visual change escaping the render pass. Harness rule
/// (docs/headless-test-dispatch-hole.md): the dispatch body returns true and the test returns the
/// dispatch task.
/// </summary>
public sealed class HexSurfaceTests
{
    /// <summary>A document of any length whose bytes are their offset's low byte, counting the
    /// rows it was asked for.</summary>
    private sealed class FakeDocument(long length) : IHexDocument
    {
        public long Length { get; set; } = length;

        public HashSet<long> RowsRead { get; } = [];

        public int ReadRow(long row, Span<byte> into)
        {
            long start = HexRows.StartOf(row);
            if (start >= Length)
                return 0;

            RowsRead.Add(row);
            int count = (int)Math.Min(HexRows.BytesPerRow, Length - start);
            for (int i = 0; i < count; i++)
                into[i] = (byte)(start + i);
            return count;
        }

        public HexCursor Cursor => field ??= new HexCursor(() => Length);

        public bool TryReadSelection(out byte[] bytes)
        {
            bytes = [];
            return false;
        }
    }

    private const int WindowHeight = 440;

    private static async Task PumpAsync()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private static Task With(FakeDocument document, Func<HexSurface, Window, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HexSurfaceTests).Assembly);
        return session.Dispatch(async () =>
        {
            var surface = new HexSurface { FontSize = 13 };
            var window = new Window { Width = 800, Height = WindowHeight, Content = surface };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                surface.Document = document;
                window.CaptureRenderedFrame();
                await body(surface, window);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    private static int ScreenRows => (int)(WindowHeight / RowSurface.RowHeight) + 1;

    [Fact]
    public Task OnlyTheRowsOnScreen_AreRead() => With(new FakeDocument(10_000_000), (surface, _) =>
    {
        Assert.InRange(surface.CachedLayoutCount, 1, ScreenRows);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ScrollingThenPainting_LaysOutTheNewRowsInsideTheFrame_AndDropsTheOld() => With(new FakeDocument(10_000_000), (surface, window) =>
    {
        var document = (FakeDocument)surface.Document!;
        surface.ScrollToFraction(0.5);

        // No layout pass between: the rows newly on screen are first reached while painting.
        window.CaptureRenderedFrame();

        Assert.InRange(surface.CachedLayoutCount, 1, ScreenRows);
        Assert.Contains(surface.TopRow, document.RowsRead);
        Assert.InRange(surface.TopRow, HexRows.RowCount(document.Length) / 2 - 1, HexRows.RowCount(document.Length) / 2 + 1);
        return Task.CompletedTask;
    });

    [Fact]
    public Task FortyGigabytes_ScrollsExactlyToItsLastRow() => With(new FakeDocument(40L * 1024 * 1024 * 1024 + 5), (surface, window) =>
    {
        var document = (FakeDocument)surface.Document!;
        surface.ScrollToEnd();
        window.CaptureRenderedFrame();

        Assert.True(surface.ShowsEnd);
        Assert.Contains(HexRows.RowCount(document.Length) - 1, document.RowsRead);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AGrowingDocument_CanBeScrolledIntoWhatArrived() => With(new FakeDocument(160), (surface, window) =>
    {
        var document = (FakeDocument)surface.Document!;
        Assert.Equal(1, surface.ViewportFraction);

        document.Length = 1_000_000;
        surface.ScrollToEnd();
        window.CaptureRenderedFrame();

        Assert.Contains(HexRows.RowCount(document.Length) - 1, document.RowsRead);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EachByte_IsColouredByItsClass_InBothColumns() => With(new FakeDocument(0), (surface, _) =>
    {
        var muted = new SolidColorBrush(Colors.Gray);
        var text = new SolidColorBrush(Colors.White);
        var other = new SolidColorBrush(Colors.Orange);
        surface.GutterForeground = muted;
        surface.Foreground = text;
        surface.OtherByteForeground = other;
        surface.Document = new BytesDocument([0x00, (byte)'A', 0xFF, (byte)'B', 0x00]);

        var characters = surface.LaidOutCharacters(0);

        Assert.Same(muted, BrushAt(surface.LaidOutByte(0x00), 0));
        Assert.Same(text, BrushAt(surface.LaidOutByte((byte)'A'), 0));
        Assert.Same(other, BrushAt(surface.LaidOutByte(0xFF), 0));
        Assert.Equal("FF", surface.LaidOutByte(0xFF).TextLines[0].TextRuns.Single().Text.ToString());
        Assert.Same(muted, BrushAt(characters, 0));
        Assert.Same(text, BrushAt(characters, 1));
        Assert.Same(other, BrushAt(characters, 2));
        Assert.Same(muted, BrushAt(characters, 4));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ZeroBytes_TakeTheirOwnFainterColour() => With(new FakeDocument(0), (surface, _) =>
    {
        var offsets = new SolidColorBrush(Colors.Gray);
        var zero = new SolidColorBrush(Colors.LightGray);
        surface.GutterForeground = offsets;
        surface.ZeroByteForeground = zero;
        surface.Document = new BytesDocument([0x00, (byte)'A']);

        Assert.Same(zero, BrushAt(surface.LaidOutByte(0x00), 0));
        Assert.Same(zero, BrushAt(surface.LaidOutCharacters(0), 0));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheBytesUnderThePointer_IsTheSameInEitherPanel() => With(new FakeDocument(1000), (surface, _) =>
    {
        foreach (int column in new[] { 0, 7, 8, 15 })
        {
            surface.HoverAt(surface.CentreOfByte(column, inCharacters: false));
            Assert.Equal(column, surface.HoveredOffset);

            surface.HoverAt(surface.CentreOfByte(column, inCharacters: true));
            Assert.Equal(column, surface.HoveredOffset);
        }

        // The gutter and the header are no byte.
        surface.HoverAt(new Point(4, 60));
        Assert.Null(surface.HoveredOffset);
        surface.HoverAt(new Point(surface.CentreOfByte(3, inCharacters: false).X, 4));
        Assert.Null(surface.HoveredOffset);
        return Task.CompletedTask;
    });

    [Fact]
    public Task MovingToAnotherByte_IsReported_AndScrollingLetsItGo() => With(new FakeDocument(100_000), (surface, _) =>
    {
        var reported = new List<long?>();
        surface.HoveredOffsetChanged += (_, _) => reported.Add(surface.HoveredOffset);

        surface.HoverAt(surface.CentreOfByte(3, inCharacters: false));
        surface.HoverAt(surface.CentreOfByte(3, inCharacters: true));
        surface.ScrollByPixels(RowSurface.RowHeight * 10);

        Assert.Equal([3, null], reported);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ThePointerPastTheEnd_MarksNothing() => With(new FakeDocument(3), (surface, _) =>
    {
        surface.HoverAt(surface.CentreOfByte(2, inCharacters: false));
        Assert.Equal(2, surface.HoveredOffset);
        surface.HoverAt(surface.CentreOfByte(5, inCharacters: false));
        Assert.Null(surface.HoveredOffset);
        return Task.CompletedTask;
    });

    /// <summary>The brush the character at <paramref name="index"/> is drawn in.</summary>
    private static IBrush? BrushAt(TextLayout layout, int index)
    {
        int at = 0;
        foreach (var run in layout.TextLines[0].TextRuns)
        {
            if (index < at + run.Length)
                return run.Properties?.ForegroundBrush;
            at += run.Length;
        }

        return null;
    }

    private sealed class BytesDocument(byte[] bytes) : IHexDocument
    {
        public long Length => bytes.Length;

        public int ReadRow(long row, Span<byte> into)
        {
            long start = HexRows.StartOf(row);
            if (start >= bytes.Length)
                return 0;
            int count = (int)Math.Min(HexRows.BytesPerRow, bytes.Length - start);
            bytes.AsSpan((int)start, count).CopyTo(into);
            return count;
        }

        public HexCursor Cursor => field ??= new HexCursor(() => Length);

        public bool TryReadSelection(out byte[] selected)
        {
            selected = Cursor.Caret is { IsEmpty: false } caret ? bytes[(int)caret.Start..(int)caret.End] : [];
            return selected.Length > 0;
        }
    }

    [Fact]
    public Task AClick_PutsTheCaretOnTheByte_InEitherPanel_AndPicksThePanelTheKeysActIn() => With(new FakeDocument(1000), (surface, _) =>
    {
        surface.ClickAt(surface.CentreOfByte(9, inCharacters: true, screenRow: 2));
        Assert.Equal(HexCaret.At(41), surface.Document!.Cursor.Caret);
        Assert.True(surface.KeysInCharacters);

        surface.ClickAt(surface.CentreOfByte(3, inCharacters: false));
        Assert.Equal(HexCaret.At(3), surface.Document!.Cursor.Caret);
        Assert.False(surface.KeysInCharacters);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ADrag_SelectsBothEnds_EitherWay_AndAShiftClickExtends() => With(new FakeDocument(1000), (surface, _) =>
    {
        var cursor = surface.Document!.Cursor;
        surface.DragBetween(surface.CentreOfByte(4, inCharacters: false), surface.CentreOfByte(2, inCharacters: false, screenRow: 1));
        Assert.Equal((4L, 19L), (cursor.Caret!.Value.Start, cursor.Caret!.Value.End));

        surface.DragBetween(surface.CentreOfByte(4, inCharacters: true, screenRow: 1), surface.CentreOfByte(1, inCharacters: true));
        Assert.Equal((1L, 21L), (cursor.Caret!.Value.Start, cursor.Caret!.Value.End));

        surface.ClickAt(surface.CentreOfByte(2, inCharacters: false));
        surface.ClickAt(surface.CentreOfByte(5, inCharacters: true), extend: true);
        Assert.Equal((2L, 6L), (cursor.Caret!.Value.Start, cursor.Caret!.Value.End));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ADragPastThePanel_SelectsToTheNearestByte() => With(new FakeDocument(1000), (surface, _) =>
    {
        var cursor = surface.Document!.Cursor;
        var past = new Point(surface.CentreOfByte(15, inCharacters: false).X + 3, surface.CentreOfByte(0, inCharacters: false, screenRow: 1).Y);
        surface.DragBetween(surface.CentreOfByte(0, inCharacters: false), past);

        Assert.Equal((0L, 32L), (cursor.Caret!.Value.Start, cursor.Caret!.Value.End));
        return Task.CompletedTask;
    });

    [Fact]
    public Task MovingTheCaretOffScreen_ScrollsJustEnoughToKeepIt() => With(new FakeDocument(1_000_000), (surface, window) =>
    {
        surface.MoveCaret(HexMove.PageDown);
        surface.MoveCaret(HexMove.PageDown);
        long caretRow = HexRows.RowOf(surface.Document!.Cursor.Caret!.Value.Byte);
        Assert.True(caretRow > surface.TopRow && caretRow <= surface.TopRow + ScreenRows);

        surface.MoveCaret(HexMove.DocumentEnd);
        window.CaptureRenderedFrame();
        Assert.True(surface.ShowsEnd);

        surface.MoveCaret(HexMove.DocumentStart);
        Assert.Equal(0, surface.TopRow);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheViewport_IsReportedToTheCursor_AsItScrolls() => With(new FakeDocument(1_000_000), (surface, _) =>
    {
        var cursor = surface.Document!.Cursor;
        Assert.Equal(0, cursor.ViewportRows!.Value.First);

        surface.ScrollToRow(5000);
        Assert.Equal(5000, cursor.ViewportRows!.Value.First);
        Assert.Equal(ByteRange.At(5000 * 16), cursor.Position);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ARevealOffScreen_ScrollsTheCaretIntoView() => With(new FakeDocument(1_000_000), (surface, window) =>
    {
        surface.Document!.Cursor.Reveal(new ByteRange(500_000, 10));
        window.CaptureRenderedFrame();

        long row = HexRows.RowOf(500_000);
        Assert.InRange(row, surface.TopRow, surface.TopRow + ScreenRows - 2);
        Assert.Equal(new ByteRange(500_000, 10), surface.Document!.Cursor.Position);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ARevealBeforeTheViewHasASize_WaitsForIt() => With(new FakeDocument(0), (surface, window) =>
    {
        var document = new FakeDocument(1_000_000);
        document.Cursor.Reveal(ByteRange.At(800_000));
        surface.Document = document;
        window.CaptureRenderedFrame();

        Assert.InRange(HexRows.RowOf(800_000), surface.TopRow, surface.TopRow + ScreenRows - 2);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheCaretAndSelection_DrawWhileScrolling_WithoutAVisualChangeInTheFrame() => With(new FakeDocument(1_000_000), (surface, window) =>
    {
        var cursor = surface.Document!.Cursor;
        cursor.Select(new HexCaret(100, 4000));
        surface.ScrollToRow(200);
        window.CaptureRenderedFrame();
        surface.MoveCaret(HexMove.Down, extend: true);
        window.CaptureRenderedFrame();
        Assert.Equal(4016, cursor.Caret!.Value.Offset);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnEmptyDocument_DrawsNothing() => With(new FakeDocument(0), (surface, window) =>
    {
        window.CaptureRenderedFrame();
        Assert.Equal(0, surface.CachedLayoutCount);
        Assert.Equal(0, surface.ScrollFraction);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Grouped_AClickInTheHexPanel_TakesTheGroup_AndInTheCharacters_TheByte() => With(new FakeDocument(1000), (surface, _) =>
    {
        surface.GroupSize = 4;
        surface.ClickAt(surface.CentreOfByte(6, inCharacters: false));
        Assert.Equal(HexCaret.At(4), surface.Document!.Cursor.Caret);

        surface.ClickAt(surface.CentreOfByte(6, inCharacters: true));
        Assert.Equal(HexCaret.At(6), surface.Document!.Cursor.Caret);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Grouped_EveryByteIsStillUnderThePointer_InBothPanels() => With(new FakeDocument(1000), (surface, _) =>
    {
        foreach (int size in HexRows.GroupSizes)
        {
            surface.GroupSize = size;
            for (int column = 0; column < HexRows.BytesPerRow; column++)
            {
                surface.HoverAt(surface.CentreOfByte(column, inCharacters: false));
                Assert.Equal(column, surface.HoveredOffset);
            }
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Grouped_ADragTakesWholeGroups() => With(new FakeDocument(1000), (surface, _) =>
    {
        surface.GroupSize = 8;
        surface.DragBetween(surface.CentreOfByte(3, inCharacters: false), surface.CentreOfByte(9, inCharacters: false, screenRow: 1));
        Assert.Equal((0L, 32L), (surface.Document!.Cursor.Caret!.Value.Start, surface.Document!.Cursor.Caret!.Value.End));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ChangingTheGrouping_LaysTheRowsOutAgain_AndDrawsAPartialLastGroup() => With(new FakeDocument(1_000_003), (surface, window) =>
    {
        surface.ScrollToEnd();
        window.CaptureRenderedFrame();
        var cursor = surface.Document!.Cursor;
        cursor.Place(1_000_001);

        surface.GroupSize = 4;
        surface.HoverAt(surface.CentreOfByte(0, inCharacters: false));
        window.CaptureRenderedFrame();

        // The panel narrows to the grouped text, plus a gap between each group's bytes.
        double groupedWidth = surface.HexPanelWidth;
        surface.GroupSize = 1;
        Assert.True(groupedWidth < surface.HexPanelWidth);
        surface.GroupSize = 4;

        // Not one of the sizes: the surface keeps to bytes one by one.
        surface.GroupSize = 3;
        Assert.Equal(1, surface.GroupSize);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Grouped_AGrowingDocumentsLastGroup_FillsInAsItArrives() => With(new FakeDocument(18), (surface, window) =>
    {
        var document = (FakeDocument)surface.Document!;
        surface.GroupSize = 4;
        document.Cursor.Move(HexMove.DocumentEnd, extend: false, pageRows: 5, groupSize: 4);
        Assert.Equal(HexCaret.At(16), document.Cursor.Caret);
        surface.HoverAt(surface.CentreOfByte(1, inCharacters: false, screenRow: 1));
        window.CaptureRenderedFrame();

        document.Length = 23;
        surface.InvalidateVisual();
        window.CaptureRenderedFrame();
        document.Cursor.Move(HexMove.DocumentEnd, extend: false, pageRows: 5, groupSize: 4);
        Assert.Equal(HexCaret.At(20), document.Cursor.Caret);
        surface.HoverAt(surface.CentreOfByte(6, inCharacters: false, screenRow: 1));
        Assert.Equal(22, surface.HoveredOffset);
        return Task.CompletedTask;
    });
}
