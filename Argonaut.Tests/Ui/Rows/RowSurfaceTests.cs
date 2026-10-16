using Argonaut.Ui.Rows;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;

namespace Argonaut.Tests.Ui.Rows;

/// <summary>
/// The render-pass guard every surface inherits: a surface lays out lazily, so the work it does
/// for rows it first reaches while painting runs inside the render pass, where Avalonia throws at
/// any visual change. A redraw the surface asks for there, and a notification it raises there,
/// must land once the pass has unwound - and anything asked for outside a pass happens at once.
/// Harness rule (docs/headless-test-dispatch-hole.md): the dispatch body returns true and the
/// test returns the dispatch task.
/// </summary>
public sealed class RowSurfaceTests
{
    /// <summary>A surface whose painting runs whatever lazy work the test gives it, once.</summary>
    private sealed class LazySurface : RowSurface
    {
        public Action<LazySurface>? WhileRendering { get; set; }

        public int Renders { get; private set; }

        public override double ScrollFraction => 0;

        public override double ViewportFraction => 1;

        public override bool ShowsEnd => true;

        public override void ScrollByPixels(double delta)
        {
        }

        public override void ScrollToFraction(double fraction)
        {
        }

        public override void ScrollToEnd()
        {
        }

        public void Widen(double width) => RecordRowWidth(width);

        public void WhenSafe(Action action) => OutsideRenderPass(action);

        protected override void RenderRows(DrawingContext context)
        {
            Renders++;
            var work = WhileRendering;
            WhileRendering = null;
            work?.Invoke(this);
        }
    }

    private static async Task PumpAsync()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private static Task With(Func<LazySurface, Border, Window, Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RowSurfaceTests).Assembly);
        return session.Dispatch(async () =>
        {
            var surface = new LazySurface();
            var neighbour = new Border { Height = 20 };
            var panel = new DockPanel();
            DockPanel.SetDock(neighbour, Avalonia.Controls.Dock.Bottom);
            panel.Children.Add(neighbour);
            panel.Children.Add(surface);
            var window = new Window { Width = 400, Height = 300, Content = panel };
            try
            {
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                window.CaptureRenderedFrame();
                await body(surface, neighbour, window);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    /// <summary>Without the guard, this is the crash itself: Avalonia throws "Visual was invalidated
    /// during the render pass" out of the frame. Whether the redraw then lands is not asserted by
    /// counting frames, which the headless compositor coalesces depending on what else ran; the
    /// deferral itself is what the next test pins down.</summary>
    [Fact]
    public Task ARedrawAskedForMidRender_DoesNotFailThePass() => With(async (surface, _, window) =>
    {
        bool painted = false;
        surface.WhileRendering = s =>
        {
            s.InvalidateVisual();
            painted = true;
        };
        surface.InvalidateVisual();
        window.CaptureRenderedFrame();
        await PumpAsync();

        Assert.True(painted);
    });

    [Fact]
    public Task ANotificationRaisedMidRender_ArrivesOnceThePassHasUnwound() => With(async (surface, neighbour, window) =>
    {
        bool arrived = false;
        bool arrivedMidRender = false;
        surface.WidestRowWidthChanged += (_, _) =>
        {
            arrived = true;
            neighbour.InvalidateVisual(); // what a host does: resize another control to match
        };

        surface.WhileRendering = s =>
        {
            s.Widen(500);
            arrivedMidRender = arrived;
        };
        surface.InvalidateVisual();
        window.CaptureRenderedFrame();
        await PumpAsync();

        Assert.False(arrivedMidRender);
        Assert.True(arrived);
        Assert.Equal(500, surface.WidestRowWidth);
    });

    [Fact]
    public Task OutsideARenderPass_ItActsAtOnce() => With((surface, _, _) =>
    {
        bool ran = false;
        surface.WhenSafe(() => ran = true);
        Assert.True(ran);

        bool notified = false;
        surface.WidestRowWidthChanged += (_, _) => notified = true;
        surface.Widen(300);
        Assert.True(notified);
        return Task.CompletedTask;
    });
}
