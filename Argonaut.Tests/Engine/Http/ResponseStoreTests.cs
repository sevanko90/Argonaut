using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Http;

/// <summary>
/// The response folder: which kept responses go when the budget is exceeded, and what a sweep
/// clears away.
/// </summary>
public sealed class ResponseStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static KeptResponse Kept(string id, long bytes, int minutesAgo, bool pinned = false)
        => new(id, id + ".response", bytes, Now.AddMinutes(-minutesAgo), pinned);

    private static string[] Evicted(IEnumerable<KeptResponse> kept, long budget, string? keep = null)
        => ResponseStore.ChooseEvictions(kept.ToList(), budget, keep).Select(k => k.EntryId).ToArray();

    [Fact]
    public void WithinBudget_NothingGoes()
    {
        Assert.Empty(Evicted([Kept("a", 400, 3), Kept("b", 500, 1)], budget: 1000));
    }

    [Fact]
    public void OverBudget_TheOldestUnpinnedGoFirst()
    {
        var kept = new[] { Kept("old", 400, 30), Kept("older-pinned", 400, 60, pinned: true), Kept("new", 400, 1) };

        Assert.Equal(["old"], Evicted(kept, budget: 1000));
    }

    [Fact]
    public void PinnedGo_OnlyWhenUnpinnedAreNotEnough()
    {
        var kept = new[] { Kept("pinned-old", 600, 60, pinned: true), Kept("pinned-new", 300, 10, pinned: true), Kept("unpinned", 300, 1) };

        Assert.Equal(["unpinned", "pinned-old"], Evicted(kept, budget: 500));
    }

    [Fact]
    public void TheResponseJustKept_IsNeverChosen()
    {
        var kept = new[] { Kept("just-now", 800, 0), Kept("other", 400, 5) };

        Assert.Equal(["other"], Evicted(kept, budget: 1000, keep: "just-now"));
    }

    [Fact]
    public void TheBudget_IsOneGigabyte()
    {
        Assert.Equal(1L << 30, ResponseStore.Budget);
    }

    [Fact]
    public void Sweeping_DeletesWhatNoEntryPointsTo_AndLeavesTheRest()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "kept.response"), "x");
        File.WriteAllText(Path.Combine(folder, "orphan.response"), "x");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
        var store = new ResponseStore(folder);

        store.Sweep(new HashSet<string> { "kept.response" });

        Assert.Equal(["kept.response", "notes.txt"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public void PathOf_FindsAKeptFile_AndNothingForOneThatIsGoneOrNotOneOfOurs()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.response"), "x");
        var store = new ResponseStore(folder);

        Assert.Equal(Path.Combine(folder, "a.response"), store.PathOf("a.response"));
        Assert.Null(store.PathOf("gone.response"));
        Assert.Null(store.PathOf("../escape.response"));
    }
}
