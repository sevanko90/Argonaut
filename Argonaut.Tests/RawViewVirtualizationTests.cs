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
    /// <summary>
    /// The extent must follow the row count the instant it changes, not when something next
    /// happens to refresh a cached copy.
    ///
    /// This is the bug behind a reveal landing short on a large file. A jump-to-offset resolves
    /// as soon as the scan's published rows cover the target byte, which can be a whole growth
    /// tick before a cached extent catches up - and the host clamps the offset it is handed
    /// against that stale extent. At full scan speed one 120ms tick is over a million rows, so
    /// the scroll stops roughly a million rows short of where it was asked to go and stays there.
    /// </summary>
    [Fact]
    public Task TheExtentTracksTheRowCountWithoutWaitingForARefresh()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new RawViewModel();
            Window? window = null;
            try
            {
                await vm.LoadAsync(WriteBigFile());

                var view = new RawView { DataContext = vm };
                window = new Window { Width = 900, Height = 600, Content = view };
                window.Show();
                await PumpAsync();
                window.UpdateLayout();

                var surface = SurfaceOf(window);

                // Let the scan finish, then read the extent with NO pump and NO layout pass in
                // between: nothing has had a chance to refresh anything.
                await vm.IndexingTask;

                Assert.Equal(vm.RowCount * RawTextSurface.RowHeight, surface.Extent.Height);
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
    /// A reveal asked for while the row it names does not exist yet must land once it does,
    /// rather than being applied once against a document that is still too short.
    ///
    /// Driven through a wrap-width change, which is the deterministic way to get this state: it
    /// restarts the index synchronously, so the row count drops to near zero and climbs again,
    /// and a reveal issued in that window names a row that genuinely is not there. Waiting for a
    /// background scan to still be running instead would be a race - a few MB indexes faster than
    /// the test can reach the next line.
    /// </summary>
    [Fact]
    public Task ARevealIssuedBeforeTheRowExists_StillLands()
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

                // Restart the index; the row count collapses and starts climbing again.
                vm.SetWrapWidth(80);
                int target = 30_000;
                Assert.True(vm.RowCount < target, $"expected a restarted scan to be short of {target}, was {vm.RowCount}");

                surface.RevealRow(target);

                await vm.IndexingTask;
                for (int i = 0; i < 20; i++)
                {
                    await PumpAsync();
                    window.UpdateLayout();
                }

                Assert.True(vm.RowCount > target, "the file should index to more rows than the target");
                Assert.InRange(target, surface.RealizedRowRange.First, surface.RealizedRowRange.Last);
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
    /// A reveal centres its row rather than scraping it into the bottom edge: the target of a
    /// jump wants context on both sides, and the bottom line of a window is where the eye has to
    /// hunt for it.
    /// </summary>
    [Fact]
    public Task RevealCentresTheRowRatherThanScrollingMinimally()
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
                int target = 20_000;

                surface.RevealRow(target);
                await PumpAsync();
                window.UpdateLayout();

                var range = surface.RealizedRowRange;
                Assert.InRange(target, range.First, range.Last);

                // Roughly as many rows above the target as below it.
                int above = target - range.First;
                int below = range.Last - target;
                Assert.True(Math.Abs(above - below) <= 2,
                    $"target sits {above} rows from the top of the viewport and {below} from the bottom");
                Assert.True(above > 5, "the target is not centred - it is near the top or bottom edge");
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
    /// Minimal scrolling stays minimal - what caret movement uses. Arrowing off the bottom edge
    /// must advance by a row, not leap half a screen.
    /// </summary>
    [Fact]
    public Task ScrollRowIntoViewMovesAsLittleAsPossible()
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
                int justBelow = surface.RealizedRowRange.Last + 1;

                surface.ScrollRowIntoView(justBelow);
                await PumpAsync();
                window.UpdateLayout();

                // It comes into view at the bottom, having moved by about one row - so the top
                // of the viewport is still row 0 or the one after it, not half a screen away.
                Assert.Equal(justBelow, surface.RealizedRowRange.Last);
                Assert.InRange(surface.RealizedRowRange.First, 0, 1);
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
    /// The question a user actually has: does moving around a huge document for a long time cost
    /// memory that is never given back? Everything the raw view holds while scrolling is supposed
    /// to be either bounded or transient - the row collection's LRU, the surface's layout and
    /// decode caches, and the strings the status gutter formats per caret move - so travelling a
    /// long way must leave the heap where it started.
    ///
    /// Phrased as identical repeated phases rather than "warm up, then measure once". A session
    /// pays a one-off cost on first use that has nothing to do with distance travelled - the
    /// Unicode name table inflates, pools fill, the text stack builds its caches - and a single
    /// before/after around that reads as a leak. A leak climbs phase after phase; this must not.
    ///
    /// The two assertions do different jobs, and the cache one is the sharper of them. Cache sizes
    /// are deterministic: they are the thing that would grow with distance travelled, and they are
    /// checked exactly. The heap figure wanders by a few MB between runs whatever the code does,
    /// so its threshold is set to catch the failure that matters - retaining something per row
    /// visited, which over 16,000 distinct rows would be tens of MB - rather than to police the
    /// noise floor.
    /// </summary>
    [Fact]
    public Task ScrollingAndMovingTheCaretAcrossTheDocument_DoNotGrowTheHeap()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RawViewVirtualizationTests).Assembly);
        return session.Dispatch(async () =>
        {
            const int VisitsPerPhase = 4_000;

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
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First();
                int rowCount = vm.RowCount;

                void Visit(int row)
                {
                    scroller.Offset = new Vector(0, row * RawTextSurface.RowHeight);
                    window.UpdateLayout();

                    // What the gutter does on every caret move: read the document around the caret
                    // and format the three strings it shows.
                    vm.Caret!.PlaceAt(vm.Index!.GetRowInfo(row).Start);
                    _ = vm.CaretCharacterText;
                    _ = vm.CaretPositionText;
                    _ = vm.CaretSelectionText;
                    Dispatcher.UIThread.RunJobs();
                }

                static long LiveHeap()
                {
                    for (int i = 0; i < 3; i++)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                        GC.WaitForPendingFinalizers();
                    }

                    return GC.GetTotalMemory(forceFullCollection: true);
                }

                long settled = long.MaxValue;
                var phases = new List<string>();
                for (int phase = 0; phase < 4; phase++)
                {
                    // Rows spread right across the document, so no cache stays warm by locality
                    // and each phase visits a different set.
                    for (int i = 0; i < VisitsPerPhase; i++)
                        Visit((i * 1_013 + phase * 37) % rowCount);

                    long heap = LiveHeap();
                    phases.Add($"phase {phase}: {heap / 1024.0 / 1024.0:F2}MB, layouts {surface.CachedLayoutCount}");

                    // The caches are sized by the viewport, not by how far the user has travelled.
                    // Exact, and the assertion a genuine retention bug would trip first.
                    Assert.InRange(surface.CachedLayoutCount, 1, ViewportRows * 4);
                    Assert.InRange(surface.RealizedRowCount, 1, ViewportRows);

                    // Phase 0 pays the session's one-off costs and is not a baseline. Afterwards,
                    // measured against the lowest settled reading, so one noisy phase cannot make
                    // the next one look like growth.
                    if (phase >= 1)
                    {
                        if (phase > 1)
                            Assert.True(heap < settled + 12_000_000,
                                $"heap climbed with distance travelled - {string.Join(" | ", phases)}");

                        settled = Math.Min(settled, heap);
                    }
                }

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
