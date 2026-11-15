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

        var (hex, characters) = surface.LaidOutRow(0);

        Assert.Same(muted, BrushAt(hex, 0));
        Assert.Same(text, BrushAt(hex, 3));
        Assert.Same(other, BrushAt(hex, 6));
        Assert.Same(text, BrushAt(hex, 9));
        Assert.Same(muted, BrushAt(hex, 12));
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

        var (hex, characters) = surface.LaidOutRow(0);

        Assert.Same(zero, BrushAt(hex, 0));
        Assert.Same(zero, BrushAt(characters, 0));
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
    }

    [Fact]
    public Task AnEmptyDocument_DrawsNothing() => With(new FakeDocument(0), (surface, window) =>
    {
        window.CaptureRenderedFrame();
        Assert.Equal(0, surface.CachedLayoutCount);
        Assert.Equal(0, surface.ScrollFraction);
        return Task.CompletedTask;
    });
}
