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
/// window over a multi-hundred-thousand-row file, asserting that only viewport-sized numbers
/// of rows are ever materialized. The scenario is the field-reported runaway: search-select a
/// row deep in the file (which scrolls the ListBox there), then change the wrap width - the
/// resulting live ItemsSource swap plus background re-index must not walk or realize the
/// whole collection.
/// </summary>
[Collection("AppDataPaths")]
public sealed class RawViewVirtualizationTests : IDisposable
{
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

    private static int RealizedContainerCount(Window window)
        => window.GetVisualDescendants().OfType<ListBoxItem>().Count();

    [Fact]
    public Task WrapChange_WithDeepSelection_StaysVirtualized()
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

                // Baseline: initial bind must realize only a viewport's worth.
                Assert.InRange(vm.Rows.MaterializedRowCount, 1, 500);
                Assert.InRange(RealizedContainerCount(window), 1, 200);

                // Simulate a search reveal deep in the file - selection + auto-scroll.
                vm.SelectRow(initialRowCount - 5);
                await PumpAsync();
                window.UpdateLayout();
                Assert.InRange(vm.Rows.MaterializedRowCount, 1, 2_000);

                // The reported runaway: re-wrap while selected/scrolled deep.
                vm.SetWrapWidth(80);
                var swapped = vm.Rows;

                var listBox = view.GetVisualDescendants().OfType<ListBox>().First();
                var probes = new List<string>();
                int lastMaterialized = 0;
                void Probe(string phase, int i)
                {
                    int materialized = swapped.MaterializedRowCount;
                    var scroll = listBox.Scroll;
                    probes.Add($"{phase} {i}: rows={vm.RowCount} mat={materialized} (+{materialized - lastMaterialized}) " +
                               $"offY={scroll?.Offset.Y:F0} extentH={scroll?.Extent.Height:F0} containers={RealizedContainerCount(window)}");
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
                // materializations / realized containers. Viewport-sized churn is fine.
                Assert.True(swapped.MaterializedRowCount <= 5_000,
                    $"walked {swapped.MaterializedRowCount} rows\n--- probes:\n{string.Join("\n", probes)}");
                Assert.InRange(RealizedContainerCount(window), 1, 200);
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
