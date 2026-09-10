using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

[assembly: AvaloniaTestApplication(typeof(Argonaut.Tests.HeadlessTestApp))]

namespace Argonaut.Tests;

public class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Headless UI regression tests for the raw viewer's virtualization: a real RawView in a real
/// window over a multi-hundred-thousand-row file, asserting that only viewport-sized numbers of
/// rows are ever materialized. The scenario is the field-reported runaway: reveal a row deep in
/// the file (which scrolls there), then change the wrap width - the resulting row-set swap plus
/// background re-index must not walk or realize the whole document.
///
/// These assertions used to count <c>ListBoxItem</c>s in the visual tree. <see cref="RawTextSurface"/>
/// has no per-row controls to count, so they now read the surface's own realized-row range - which
/// is a tighter check rather than a weaker one, because it says <i>which</i> rows are held and not
/// merely how many. The surface deliberately decides that range during layout rather than during
/// rendering, which is what makes it observable at all here: headless has no renderer.
/// </summary>
[Collection("AppDataPaths")]
public sealed class RawViewVirtualizationTests : IDisposable
{
    /// <summary>A 600px-tall window holds this many 22px rows, plus a partial one.</summary>
    private const int ViewportRows = (int)(600 / RawTextSurface.RowHeight) + 2;

    private readonly string tempDir;

    public RawViewVirtualizationTests()
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

    private string WriteBigFile()
    {
        // ~5MB of 130-char lines: ~40k rows at wrap 160, ~80k at wrap 80.
        var sb = new StringBuilder(6 * 1024 * 1024);
        var line = new string('x', 120);
        for (int i = 0; i < 40_000; i++)
            sb.Append($"L{i:D8} ").Append(line).Append('\n');

        string path = Path.Combine(tempDir, "big.txt");
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

    [Fact]
    public Task WrapChange_WithDeepReveal_StaysVirtualized()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteBigFile());
                await vm.IndexingTask;
                int initialRowCount = vm.RowCount;
                Assert.True(initialRowCount > 30_000);

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 900, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var surface = SurfaceOf(window);

                // Baseline: initial bind must realize only a viewport's worth.
                Assert.InRange(vm.Rows.MaterializedRowCount, 1, 500);
                Assert.InRange(surface.RealizedRowCount, 1, ViewportRows);
                Assert.Equal(0, surface.RealizedRowRange.First);

                // A search reveal deep in the file: scrolls there, and must not walk to it.
                vm.SelectRow(initialRowCount - 5);
                await PumpAsync();
                window.UpdateLayout();
                Assert.InRange(vm.Rows.MaterializedRowCount, 1, 2_000);
                Assert.InRange(surface.RealizedRowCount, 1, ViewportRows);

                // The rows held must be the ones on screen, not merely few in number.
                Assert.True(surface.RealizedRowRange.First > initialRowCount - 500,
                    $"revealed row {initialRowCount - 5} but the surface is holding rows from {surface.RealizedRowRange.First}");

                // The reported runaway: re-wrap while scrolled deep.
                vm.SetWrapWidth(80);
                var swapped = vm.Rows;

                var probes = new List<string>();
                int lastMaterialized = 0;
                void Probe(string phase, int i)
                {
                    int materialized = swapped.MaterializedRowCount;
                    probes.Add($"{phase} {i}: rows={vm.RowCount} mat={materialized} (+{materialized - lastMaterialized}) " +
                               $"realized={surface.RealizedRowCount} range={surface.RealizedRowRange}");
                    lastMaterialized = materialized;
                }

                // Pump until the background re-index completes and growth notifications drain.
                for (int i = 0; i < 200 && !vm.IndexingTask.IsCompleted; i++)
                {
                    await PumpAsync();
                    Probe("scan", i);
                }

                await vm.IndexingTask;
                for (int i = 0; i < 10; i++)
                {
                    await PumpAsync();
                    window.UpdateLayout();
                    Probe("drain", i);
                }

                Assert.True(vm.RowCount > initialRowCount, "wrap 80 should produce more rows than wrap 160");

                // The failure mode is a whole-collection walk: hundreds of thousands of
                // materializations. Viewport-sized churn is fine.
                Assert.True(swapped.MaterializedRowCount <= 5_000,
                    $"walked {swapped.MaterializedRowCount} rows\n--- probes:\n{string.Join("\n", probes)}");
                Assert.InRange(surface.RealizedRowCount, 1, ViewportRows);
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
    /// Scrolling to the far end of a very long document must hold only the rows at that end. This
    /// is the property the old ListBoxItem count could only approximate - it could tell you the
    /// panel held few containers, never that they were the right ones.
    /// </summary>
    [Fact]
    public Task ScrollingToTheEnd_HoldsOnlyTheRowsThere()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteBigFile());
                await vm.IndexingTask;

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 900, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var surface = SurfaceOf(window);
                int lastRow = vm.RowCount - 1;

                surface.ScrollRowIntoView(lastRow);
                await PumpAsync();
                window.UpdateLayout();

                Assert.InRange(surface.RealizedRowCount, 1, ViewportRows);
                Assert.Equal(lastRow, surface.RealizedRowRange.Last);
                Assert.True(surface.RealizedRowRange.First > lastRow - ViewportRows - 1,
                    $"holding rows from {surface.RealizedRowRange.First} when only the last screenful should be live");
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
