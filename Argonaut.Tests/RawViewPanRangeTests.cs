using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests;

/// <summary>
/// The raw view's horizontal pan range must follow the text, not the wrap width. It used to be
/// estimated as "wrap-width bytes x the advance of W", which overshoots twice over - a row has
/// fewer characters than bytes wherever the content is not ASCII, and no real text averages the
/// widest glyph in the font - so at wrap 160 the bar offered a long pan into empty space.
/// </summary>
[Collection("AppDataPaths")]
public sealed class RawViewPanRangeTests : IDisposable
{
    private readonly string tempDir;

    public RawViewPanRangeTests()
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

    private string WriteFile(string line, int lineCount)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lineCount; i++)
            sb.Append(line).Append('\n');

        string path = Path.Combine(tempDir, "rows.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static async Task PumpAsync(int milliseconds = 50)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static RawTextSurface SurfaceOf(Window window)
        => window.GetVisualDescendants().OfType<RawTextSurface>().First();

    private static ScrollBar PanBarOf(Window window)
        => window.GetVisualDescendants().OfType<ScrollBar>()
            .First(bar => bar.Orientation == Orientation.Horizontal && bar.Name == "PanScrollBar");

    /// <summary>
    /// Rows that fit the viewport must offer no pan at all, even at a wrap width whose byte count
    /// would suggest otherwise. This is the reported bug in its simplest form.
    /// </summary>
    [Fact]
    public Task ShortRows_AtWideWrap_OfferNoPan()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewPanRangeTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteFile(new string('i', 20), 200));
                await vm.IndexingTask;
                vm.SetWrapWidth(160);
                await vm.IndexingTask;

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 1200, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();
                window.UpdateLayout();

                var surface = SurfaceOf(window);
                double textViewport = surface.Bounds.Width - (2 * RawTextSurface.ContentPaddingX)
                    - RawTextSurface.LineNumberColumnWidth - RawTextSurface.WrapGutterWidth;

                Assert.True(surface.WidestRowWidth > 0, "no row was measured");
                Assert.True(surface.WidestRowWidth < textViewport,
                    $"20-character rows measured {surface.WidestRowWidth}px against a {textViewport}px viewport");

                // The bar reacts a dispatcher turn after the measurement - see the surface's
                // NotifyWidestRowWidthChanged.
                await PumpAsync();
                Assert.False(PanBarOf(window).IsVisible);
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// When rows do overflow, the pan range must stop where the text stops: the maximum is the
    /// widest measured row minus the viewport, with nothing added for bytes that never became
    /// characters or for glyphs wider than the ones actually drawn.
    /// </summary>
    [Fact]
    public Task PanRange_StopsAtTheWidestMeasuredRow()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewPanRangeTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteFile(new string('m', 150), 200));
                await vm.IndexingTask;

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 500, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();
                window.UpdateLayout();

                var surface = SurfaceOf(window);
                var panBar = PanBarOf(window);
                await PumpAsync();
                double textViewport = surface.Bounds.Width - (2 * RawTextSurface.ContentPaddingX)
                    - RawTextSurface.LineNumberColumnWidth - RawTextSurface.WrapGutterWidth;

                Assert.True(panBar.IsVisible, "150-character rows in a 500px window should pan");
                Assert.Equal(surface.WidestRowWidth - textViewport, panBar.Maximum, precision: 3);
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// The measurement is a high-water mark: scrolling off the widest row must not shrink the
    /// range under the user's thumb, and scrolling onto a wider one must grow it.
    /// </summary>
    [Fact]
    public Task WidestRowWidth_GrowsWithScrolling_AndNeverShrinks()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewPanRangeTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel();
            Window? window = null;
            try
            {
                // Narrow rows first, one very wide row far enough down to need scrolling to reach.
                var sb = new StringBuilder();
                for (int i = 0; i < 400; i++)
                    sb.Append(new string('i', 10)).Append('\n');
                sb.Append(new string('M', 150)).Append('\n');
                string path = Path.Combine(tempDir, "mixed.txt");
                File.WriteAllText(path, sb.ToString());

                await vm.LoadAsync(path);
                await vm.IndexingTask;

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 500, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();
                window.UpdateLayout();

                var surface = SurfaceOf(window);
                double narrowWidth = surface.WidestRowWidth;
                Assert.True(narrowWidth > 0, "no row was measured");

                // Two cycles: the reveal is applied during arrange, so the rows it scrolled to
                // are realized - and measured - by the pass after it.
                vm.SelectRow(vm.RowCount - 1);
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();
                window.UpdateLayout();

                double wideWidth = surface.WidestRowWidth;
                Assert.True(wideWidth > narrowWidth,
                    $"the wide row should have grown the measurement: {narrowWidth} -> {wideWidth}");

                // Back to the narrow rows: the range holds rather than collapsing mid-scroll.
                vm.SelectRow(0);
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();
                window.UpdateLayout();
                Assert.Equal(wideWidth, surface.WidestRowWidth, precision: 3);

                // A new wrap width invalidates every row, so the measurement starts again.
                vm.SetWrapWidth(80);
                await vm.IndexingTask;
                await PumpAsync();
                window.UpdateLayout();
                Assert.True(surface.WidestRowWidth < wideWidth,
                    $"a re-wrap should have reset the high-water mark, still {surface.WidestRowWidth}");
                return true;
            }
            finally
            {
                window?.Close();
                vm.Dispose();
            }
        }, CancellationToken.None);
    }
}
