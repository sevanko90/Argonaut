using System.Diagnostics;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.Search;

namespace Argonaut.Tests;

/// <summary>
/// TEMPORARY diagnostic harness for the rapid-Enter hang / wrap-to-top on the 25MB geojson
/// pair. Skips silently when those local files are absent. Not a committed regression test.
/// </summary>
public class DiffFindRepro
{
    private const string Left = "/Users/marcevans/testData/geojson-sample-25mb.json";
    private const string Right = "/Users/marcevans/testData/geojson-sample-changed-25mb.json";
    private const string Term = "2133.6";

    private static string Trunc(string v) => v.Length <= 40 ? v : v[..40] + "…";

    private static bool Available => File.Exists(Left) && File.Exists(Right);

    private static async Task<(JsonDiffViewModel Vm, FindController Controller, List<string?> Statuses)> LoadAsync()
    {
        var vm = new JsonDiffViewModel();
        await vm.LoadAsync(Left, Right);
        try { await vm.IndexingTask; } catch { }

        // Wait for the diff to actually finish publishing records.
        var sw = Stopwatch.StartNew();
        while (!vm.DiffComplete && sw.Elapsed < TimeSpan.FromSeconds(60))
            await Task.Delay(50);

        vm.Rows.ChangesOnly = true;
        vm.Rows.ChangesOnly = false;

        var statuses = new List<string?>();
        var controller = new FindController(statuses.Add, () => null);
        controller.Attach(vm.CreateSearchNavigator());
        return (vm, controller, statuses);
    }

    /// <summary>Wraps the real navigator to record what key each revealed match carried.</summary>
    private sealed class Logging(ISearchNavigator inner) : ISearchNavigator
    {
        public List<(int File, long Offset, long Key)> Revealed { get; } = new();
        public Argonaut.Infrastructure.MMapFile File => inner.File;
        public IReadOnlyList<Argonaut.Infrastructure.MMapFile> Files => inner.Files;
        public void SetHighlightTerm(string? t) => inner.SetHighlightTerm(t);
        public Task RevealAsync(SearchMatch m, CancellationToken ct) => RevealAsync(0, m, ct);
        public Task RevealAsync(int i, SearchMatch m, CancellationToken ct)
        {
            Revealed.Add((i, m.Offset, inner.OrderKey(i, m) ?? -1));
            return inner.RevealAsync(i, m, ct);
        }
        public long? OrderKey(int i, SearchMatch m) => inner.OrderKey(i, m);
    }

    [Fact]
    public async Task Compare_RowGrowth_DiffVersusJsonView()
    {
        if (!Available) return;

        // --- single JSON view on the same file + term (the fast case) ---
        var jvm = new Argonaut.Features.Json.JsonViewModel();
        await jvm.LoadAsync(Left);
        try { await jvm.IndexingTask; } catch { }
        var jController = new FindController(_ => { }, () => null);
        jController.Attach(jvm.CreateSearchNavigator());
        try
        {
            for (int i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                await jController.FindAsync(Term, 1);
                sw.Stop();
                if (i % 5 == 0 || i == 19)
                    Console.WriteLine($"JSON  step {i,2} rows={jvm.Rows?.Count,8} ms={sw.ElapsedMilliseconds}");
            }
        }
        finally { await jController.DetachAsync(); jvm.Dispose(); }

        // --- diff on the pair ---
        var (vm, controller, _) = await LoadAsync();
        try
        {
            for (int i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                await controller.FindAsync(Term, 1);
                sw.Stop();
                if (i % 5 == 0 || i == 19)
                    Console.WriteLine($"DIFF  step {i,2} rows={vm.Rows.Count,8} ms={sw.ElapsedMilliseconds} pos={vm.SelectedPosition}");
            }
        }
        finally { await controller.DetachAsync(); vm.Dispose(); }
    }

    [Fact]
    public async Task Diagnose_KeyVersusPosition()
    {
        if (!Available) return;

        var vm = new JsonDiffViewModel();
        await vm.LoadAsync(Left, Right);
        try { await vm.IndexingTask; } catch { }
        var sw0 = Stopwatch.StartNew();
        while (!vm.DiffComplete && sw0.Elapsed < TimeSpan.FromSeconds(60))
            await Task.Delay(50);
        vm.Rows.ChangesOnly = true;
        vm.Rows.ChangesOnly = false;

        var logging = new Logging(vm.CreateSearchNavigator()!);
        var controller = new FindController(_ => { }, () => null);
        controller.Attach(logging);
        try
        {
            for (int i = 0; i < 30; i++)
            {
                await controller.FindAsync(Term, 1);
                var r = logging.Revealed[^1];
                var row = (JsonDiffRow)vm.Rows[vm.SelectedPosition!.Value]!;
                string shown = (row.Left?.Value ?? "") + "|" + (row.Right?.Value ?? "");
                bool onTerm = shown.Contains(Term);
                Console.WriteLine($"step {i,2} owner={r.Key >> 32,7} pos={vm.SelectedPosition,7} onTerm={onTerm,-5} kids={row.HasChildren,-5} name={row.Left?.Name ?? row.Right?.Name} val={Trunc(shown)}");
            }
        }
        finally
        {
            await controller.DetachAsync();
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Sequential_StepsForwardWithoutJumpingToTop()
    {
        if (!Available) return;

        var (vm, controller, statuses) = await LoadAsync();
        try
        {
            var positions = new List<int>();
            var times = new List<long>();

            for (int i = 0; i < 40; i++)
            {
                var sw = Stopwatch.StartNew();
                await controller.FindAsync(Term, 1);
                sw.Stop();
                times.Add(sw.ElapsedMilliseconds);
                positions.Add(vm.SelectedPosition ?? -1);
            }

            Console.WriteLine("positions: " + string.Join(",", positions));
            Console.WriteLine("ms:        " + string.Join(",", times));
            Console.WriteLine("last status: " + statuses.LastOrDefault());

            // Report every backwards step (a wrap looks like a big drop).
            for (int i = 1; i < positions.Count; i++)
            {
                if (positions[i] < positions[i - 1])
                    Console.WriteLine($"  BACKWARDS at step {i}: {positions[i - 1]} -> {positions[i]}");
            }
        }
        finally
        {
            await controller.DetachAsync();
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Overlapping_RapidEnter_DoesNotHang()
    {
        if (!Available) return;

        var (vm, controller, _) = await LoadAsync();
        try
        {
            // Exactly what the shell does on Enter: fire and forget, no awaiting.
            var running = new List<Task>();
            for (int i = 0; i < 60; i++)
            {
                running.Add(controller.FindAsync(Term, 1));
                await Task.Delay(15);
            }

            var all = Task.WhenAll(running);
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));
            Console.WriteLine(finished == all ? "ALL COMPLETED" : "*** HUNG ***");
            Assert.True(finished == all, "rapid overlapping FindAsync calls hung");
        }
        finally
        {
            await controller.DetachAsync();
            vm.Dispose();
        }
    }
}
