using Argonaut.Ui.Rows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Argonaut.Tests.Ui.Rows;

/// <summary>
/// The scrollbars every self-drawn view shares, over a surface whose position is a plain number:
/// the vertical bar follows the surface and only drives it, sits still under a held thumb, sends
/// the bottom of the track to the end, moves by rows and pages; the pan bar is sized from the
/// widest row and clamps what the surface asks for. Harness rule
/// (docs/headless-test-dispatch-hole.md): the dispatch body returns true and the test returns the
/// dispatch task.
/// </summary>
public sealed class RowScrollBarsTests
{
    /// <summary>A document of <see cref="Rows"/> fixed rows, scrolled by a pixel offset - the
    /// exact model with nothing else in the way. Records what it was asked to do.</summary>
    private sealed class FakeSurface : RowSurface
    {
        public int Rows { get; set; } = 1000;

        public double Top { get; private set; }

        public List<string> Calls { get; } = new();

        private double Extent => Rows * RowHeight;

        private double MaxTop => Math.Max(0, Extent - Bounds.Height);

        public override double ScrollFraction => Top / Extent;

        public override double ViewportFraction => Math.Min(1, Bounds.Height / Extent);

        public override bool ShowsEnd => Top >= MaxTop;

        public override void ScrollByPixels(double delta)
        {
            Calls.Add($"by {delta}");
            Move(Top + delta);
        }

        public override void ScrollToFraction(double fraction)
        {
            Calls.Add($"to {fraction}");
            Move(fraction * Extent);
        }

        public override void ScrollToEnd()
        {
            Calls.Add("end");
            Move(MaxTop);
        }

        public void Widen(double width) => RecordRowWidth(width);

        public void AskToPan(double x) => RequestPan(x);

        private void Move(double top)
        {
            Top = Math.Clamp(top, 0, MaxTop);
            NotifyScrollPosition();
        }
    }

    private sealed record Harness(FakeSurface Surface, ScrollBar Vertical, ScrollBar Pan, RowScrollBars Bars);

    private static async Task PumpAsync()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private static Task With(Func<Harness, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RowScrollBarsTests).Assembly);
        return session.Dispatch(async () =>
        {
            var surface = new FakeSurface();
            var vertical = new ScrollBar { Orientation = Avalonia.Layout.Orientation.Vertical };
            var pan = new ScrollBar { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(vertical, 1);
            Grid.SetRow(pan, 1);
            grid.Children.Add(surface);
            grid.Children.Add(vertical);
            grid.Children.Add(pan);
            var window = new Window { Width = 600, Height = 440, Content = grid };
            using var bars = new RowScrollBars(surface, vertical, pan);
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                bars.Refresh();
                await body(new Harness(surface, vertical, pan, bars));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public Task TheBarIsTheDocumentAsFractionsAndFollowsTheSurface() => With(h =>
    {
        Assert.True(h.Vertical.IsVisible);
        Assert.Equal(h.Surface.ViewportFraction, h.Vertical.ViewportSize, 9);
        Assert.Equal(1 - h.Surface.ViewportFraction, h.Vertical.Maximum, 9);

        h.Surface.ScrollByPixels(100 * RowSurface.RowHeight);

        Assert.Equal(h.Surface.ScrollFraction, h.Vertical.Value, 9);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ABarIsHiddenWhenAScreenShowsEverything() => With(h =>
    {
        h.Surface.Rows = 3;
        h.Bars.Refresh();

        Assert.False(h.Vertical.IsVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AHeldThumbIsNotMovedByTheViewItMoves() => With(h =>
    {
        h.Bars.OnVerticalScroll(ScrollEventType.ThumbTrack, 0.4);
        Assert.Equal("to 0.4", h.Surface.Calls[^1]);

        // The surface moved and said so, but the bar is not told while the thumb is held.
        double held = h.Vertical.Value;
        h.Surface.ScrollByPixels(RowSurface.RowHeight);
        Assert.Equal(held, h.Vertical.Value);

        h.Bars.OnVerticalScroll(ScrollEventType.EndScroll, 0.4);
        Assert.Equal(h.Surface.ScrollFraction, h.Vertical.Value, 9);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheBottomOfTheTrackIsTheEnd() => With(h =>
    {
        h.Bars.OnVerticalScroll(ScrollEventType.ThumbTrack, h.Vertical.Maximum);
        h.Bars.OnVerticalScroll(ScrollEventType.EndScroll, h.Vertical.Maximum);

        Assert.Equal("end", h.Surface.Calls[^1]);
        Assert.True(h.Surface.ShowsEnd);
        Assert.Equal(h.Vertical.Maximum, h.Vertical.Value);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ArrowsMoveARowAndTheTrackAPage() => With(h =>
    {
        double page = h.Surface.Bounds.Height - RowSurface.RowHeight;

        h.Bars.OnVerticalScroll(ScrollEventType.SmallIncrement, 0);
        h.Bars.OnVerticalScroll(ScrollEventType.LargeIncrement, 0);
        h.Bars.OnVerticalScroll(ScrollEventType.SmallDecrement, 0);
        h.Bars.OnVerticalScroll(ScrollEventType.LargeDecrement, 0);

        Assert.Equal(new[] { $"by {RowSurface.RowHeight}", $"by {page}", $"by {-RowSurface.RowHeight}", $"by {-page}" }, h.Surface.Calls);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ThePanBarIsSizedFromTheWidestRowAndClampsWhatTheSurfaceAsks() => With(async h =>
    {
        Assert.False(h.Pan.IsVisible);

        h.Surface.Widen(h.Surface.PanViewportWidth + 300);
        await PumpAsync();

        Assert.True(h.Pan.IsVisible);
        Assert.Equal(300, h.Pan.Maximum, 6);

        h.Surface.AskToPan(1000);
        Assert.Equal(300, h.Pan.Value, 6);
        Assert.Equal(300, h.Surface.PanOffset, 6);
    });
}
