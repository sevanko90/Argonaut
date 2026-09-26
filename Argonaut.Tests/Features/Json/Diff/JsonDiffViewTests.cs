using System.Text;
using Argonaut.Features.Json.Diff;
using Argonaut.Tests.Support;
using Argonaut.Ui.Tree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Argonaut.Tests.Features.Json.Diff;

/// <summary>
/// The diff view end to end in a headless window: the surface draws the merged tree two panes
/// wide once the comparison has rows, next change scrolls to and selects its row, and a click
/// selection fills the context bar. Harness rule (docs/headless-test-dispatch-hole.md): the
/// dispatch body returns true and the test returns the dispatch task.
/// </summary>
public sealed class JsonDiffViewTests : IDisposable
{
    private readonly string leftPath = Path.Combine(Path.GetTempPath(), $"diff-left-{Guid.NewGuid():N}.json");
    private readonly string rightPath = Path.Combine(Path.GetTempPath(), $"diff-right-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        File.Delete(leftPath);
        File.Delete(rightPath);
    }

    private static async Task PumpAsync()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    private Task WithView(string leftJson, string rightJson, Func<Window, JsonDiffViewModel, TreeSurface, Task> body)
    {
        File.WriteAllBytes(leftPath, Encoding.UTF8.GetBytes(leftJson));
        File.WriteAllBytes(rightPath, Encoding.UTF8.GetBytes(rightJson));
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(JsonDiffViewTests).Assembly);
        return session.Dispatch(async () =>
        {
            var vm = new JsonDiffViewModel();
            var window = new Window { Width = 900, Height = 500 };
            try
            {
                await vm.LoadAsync(leftPath, rightPath);
                window.Content = new JsonDiffView { DataContext = vm };
                window.Show();
                try { await vm.IndexingTask; } catch { }
                await vm.FinalRefreshTask;
                await PumpAsync();
                window.UpdateLayout();
                await PumpAsync();

                var surface = window.GetVisualDescendants().OfType<TreeSurface>().Single();
                await body(window, vm, surface);
            }
            finally
            {
                window.Close();
                vm.Dispose();
            }

            return true;
        }, CancellationToken.None);
    }

    private static string Items(int count, int changed)
    {
        var json = new StringBuilder("{\"meta\":{\"name\":\"test\"},\"items\":[");
        for (int i = 0; i < count; i++)
            json.Append(i == 0 ? "" : ",").Append($"{{\"id\":{i},\"v\":\"{(i == changed ? "changed" : "v" + i)}\"}}");
        return json.Append("]}").ToString();
    }

    [Fact]
    public Task TheSurfaceDrawsTheMergedTree() => WithView(Items(2000, -1), Items(2000, 1500), (_, vm, surface) =>
    {
        Assert.Same(vm.DiffTree, surface.Document);
        Assert.InRange(surface.RealizedRows.Count, 5, 40);
        Assert.Equal(2, vm.DiffTree!.Painter.PaneCount);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NextChangeScrollsToItsRowAndSelectsIt() => WithView(Items(2000, -1), Items(2000, 1500), async (_, vm, surface) =>
    {
        vm.GoToNextDiff();
        await PumpAsync();

        var selected = surface.SelectedRow!.Value;
        Assert.Equal(vm.SelectedRow!.Value.Start, selected.Start);
        Assert.Contains(surface.RealizedRows, r => r.Start == selected.Start);
        Assert.Equal("\"v1500\"", vm.SourcePrefix + vm.SourceChanged + vm.SourceSuffix);
        Assert.Equal("\"changed\"", vm.TargetPrefix + vm.TargetChanged + vm.TargetSuffix);
    });

    [Fact]
    public Task ChangesOnlyReseatsTheSurface() => WithView(Items(50, -1), Items(50, 20), async (_, vm, surface) =>
    {
        int before = surface.RealizedRows.Count;
        vm.Toolbar!.ChangesOnly = true;
        await PumpAsync();

        Assert.True(surface.RealizedRows.Count < before);
        Assert.All(surface.RealizedRows, r => Assert.False(((JsonDiffRowDetail)r.Detail!).RightMirrorsLeft));
    });
}
