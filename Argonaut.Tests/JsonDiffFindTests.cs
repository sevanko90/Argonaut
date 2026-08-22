using System.Text;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.Search;

namespace Argonaut.Tests;

/// <summary>
/// Find across a diff's two documents from the shell's single find bar: matches in either side
/// are found, stepped through in merged display order, and revealed by selecting the merged row
/// that shows them - which is also what keeps the panes in step, since a diff row carries both
/// sides at once.
///
/// Drives the real <see cref="FindController"/> over a real <see cref="JsonDiffSearchNavigator"/>
/// and real temp files, so the two-session stepping is exercised end to end rather than mocked.
/// </summary>
public class JsonDiffFindTests
{
    private static string WriteTemp(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private sealed class Harness : IDisposable
    {
        public JsonDiffViewModel Vm { get; init; } = null!;
        public FindController Controller { get; init; } = null!;
        public List<string?> Statuses { get; init; } = null!;
        public string LeftPath { get; init; } = null!;
        public string RightPath { get; init; } = null!;

        public List<JsonDiffRow> Rows()
        {
            var list = new List<JsonDiffRow>();
            for (int i = 0; i < Vm.Rows.Count; i++)
                list.Add((JsonDiffRow)Vm.Rows[i]!);
            return list;
        }

        public JsonDiffRow SelectedRow() => Rows()[Vm.SelectedPosition!.Value];

        public void Dispose()
        {
            // The scans hold spans over the mappings the view model is about to dispose.
            Controller.DetachAsync().GetAwaiter().GetResult();
            Vm.Dispose();
            File.Delete(LeftPath);
            File.Delete(RightPath);
        }
    }

    private static async Task<Harness> LoadAsync(string leftJson, string rightJson)
    {
        string leftPath = WriteTemp(leftJson);
        string rightPath = WriteTemp(rightJson);
        var vm = new JsonDiffViewModel();
        await vm.LoadAsync(leftPath, rightPath);
        try { await vm.IndexingTask; } catch { }

        // Same dispatcher-free rebuild nudge JsonDiffContextTests uses: in the app the growth
        // monitor does this, here the filter round-trip forces the post-diff rebuild.
        vm.Rows.ChangesOnly = true;
        vm.Rows.ChangesOnly = false;

        var statuses = new List<string?>();
        var controller = new FindController(statuses.Add, () => null);
        controller.Attach(vm.CreateSearchNavigator());

        return new Harness
        {
            Vm = vm,
            Controller = controller,
            Statuses = statuses,
            LeftPath = leftPath,
            RightPath = rightPath,
        };
    }

    [Fact]
    public async Task Find_MatchOnlyInTheLeftDocument_SelectsTheRowShowingIt()
    {
        using var h = await LoadAsync(
            """{"keep":1,"onlyleft":"needle"}""",
            """{"keep":1}""");

        await h.Controller.FindAsync("needle", 1);

        Assert.NotNull(h.Vm.SelectedPosition);
        var row = h.SelectedRow();
        Assert.Equal(DiffStatus.Removed, row.Status);
        Assert.Equal("onlyleft", row.Left!.Name);
    }

    [Fact]
    public async Task Find_MatchOnlyInTheRightDocument_SelectsTheRowShowingIt()
    {
        // The right document is the ONLY place this text exists - the single-file find of v1
        // could never have reached it.
        using var h = await LoadAsync(
            """{"keep":1}""",
            """{"keep":1,"onlyright":"needle"}""");

        await h.Controller.FindAsync("needle", 1);

        Assert.NotNull(h.Vm.SelectedPosition);
        var row = h.SelectedRow();
        Assert.Equal(DiffStatus.Added, row.Status);
        Assert.Equal("onlyright", row.Right!.Name);
    }

    [Fact]
    public async Task Find_HighlightTermIsPushedIntoTheDocument_AndClearedOnStop()
    {
        using var h = await LoadAsync("""{"a":"needle"}""", """{"a":"needle"}""");

        await h.Controller.FindAsync("needle", 1);
        Assert.Equal("needle", h.Vm.HighlightTerm);

        await h.Controller.StopAsync();
        Assert.Null(h.Vm.HighlightTerm);
    }

    /// <summary>The final count lands once both scans finish, which is a beat after the press.</summary>
    private static async Task AssertSettlesOnAsync(Harness h, string expected)
    {
        for (int i = 0; i < 200 && !h.Statuses.Contains(expected); i++)
            await Task.Delay(10);

        Assert.Contains(expected, h.Statuses);
    }

    [Fact]
    public async Task Find_CountsStopsInBothDocuments()
    {
        // "needle" on each side of a Modified leaf. That row renders BOTH panes, so both are
        // genuinely on screen and both are stops.
        using var h = await LoadAsync(
            """{"a":"needle-left"}""",
            """{"a":"needle-right"}""");

        await h.Controller.FindAsync("needle", 1);

        await AssertSettlesOnAsync(h, "1 of 2");
    }

    [Fact]
    public async Task Find_CountsPlacesItWillStop_NotTimesTheBytesOccur()
    {
        // The heart of the stop list: "needle" occurs in BOTH files, but the unchanged "outer"
        // subtree is rendered from the left document into both panes, so the right file's copy
        // is not on screen and find will never stop there. The count has to say 1, not 2 -
        // otherwise it advertises a stop that cannot be reached and the numbering skips.
        using var h = await LoadAsync(
            """{"outer":{"deep":"needle"},"x":1}""",
            """{"outer":{"deep":"needle"},"x":2}""");

        await h.Controller.FindAsync("needle", 1);

        await AssertSettlesOnAsync(h, "1 of 1");
    }

    [Fact]
    public async Task Find_StepsThroughBothDocumentsInMergedOrder_ThenWraps()
    {
        // One match per side, and the left one sits at an EARLIER merged position (the removed
        // property precedes the added one in the merged walk), so it must come first even
        // though each document is scanned independently.
        using var h = await LoadAsync(
            """{"aaa":"needle","keep":1}""",
            """{"keep":1,"zzz":"needle"}""");

        await h.Controller.FindAsync("needle", 1);
        var first = h.SelectedRow();
        Assert.Equal(DiffStatus.Removed, first.Status);
        Assert.Equal("aaa", first.Left!.Name);

        await h.Controller.FindAsync("needle", 1);
        var second = h.SelectedRow();
        Assert.Equal(DiffStatus.Added, second.Status);
        Assert.Equal("zzz", second.Right!.Name);

        // Wraps back round to the first.
        await h.Controller.FindAsync("needle", 1);
        Assert.Equal("aaa", h.SelectedRow().Left!.Name);

        // And previous walks the same ring backwards.
        await h.Controller.FindAsync("needle", -1);
        Assert.Equal("zzz", h.SelectedRow().Right!.Name);
    }

    [Fact]
    public async Task Find_RevealsAMatchHiddenInsideACollapsedContainer()
    {
        using var h = await LoadAsync(
            """{"outer":{"inner":{"deep":"needle"}},"x":1}""",
            """{"outer":{"inner":{"deep":"needle"}},"x":2}""");

        // The whole unchanged "outer" subtree is one undescended record, collapsed - the match
        // has no row at all until find opens the way to it.
        Assert.DoesNotContain(h.Rows(), r => r.Left is { Value: "\"needle\"" });

        await h.Controller.FindAsync("needle", 1);

        Assert.NotNull(h.Vm.SelectedPosition);
        var row = h.SelectedRow();
        Assert.Equal("deep", row.Left!.Name);
        Assert.Equal("\"needle\"", row.Left!.Value);
    }

    [Fact]
    public async Task Find_MirroredRegion_TheRightDocumentCopyIsNotASecondStop()
    {
        // "outer" is unchanged, so it is one undescended record walked from the LEFT document
        // into both panes - the right file's bytes there are never rendered. Both files match
        // "needle", but only one of those is on screen, so find must offer exactly one stop.
        // Before suppression the right copy became a second stop that fell back to the record
        // row ABOVE the real one, so find-next appeared to jump backwards.
        using var h = await LoadAsync(
            """{"outer":{"deep":"needle"},"x":1}""",
            """{"outer":{"deep":"needle"},"x":2}""");

        await h.Controller.FindAsync("needle", 1);
        int first = h.Vm.SelectedPosition!.Value;
        Assert.Equal("deep", h.SelectedRow().Left!.Name);

        // Stepping on wraps straight back to the same single stop - it never lands on the
        // enclosing "outer" record row.
        await h.Controller.FindAsync("needle", 1);
        Assert.Equal(first, h.Vm.SelectedPosition!.Value);

        await h.Controller.FindAsync("needle", -1);
        Assert.Equal(first, h.Vm.SelectedPosition!.Value);
    }

    [Fact]
    public async Task Find_RightOnlyTextOnAModifiedLeaf_IsStillReachable()
    {
        // Guards against over-suppressing: a Modified record's own row renders BOTH panes, so
        // text that exists only in the right document there is genuinely on screen and must
        // still be findable.
        using var h = await LoadAsync("""{"a":"cat"}""", """{"a":"zebra"}""");

        await h.Controller.FindAsync("zebra", 1);

        Assert.NotNull(h.Vm.SelectedPosition);
        Assert.Equal("\"zebra\"", h.SelectedRow().Right!.Value);
    }

    [Fact]
    public async Task Find_MovedSubtree_IsReachableAtTheEndThatRendersIt()
    {
        // A move renders its content at ONE end (the destination shows the right document), so
        // the suppression rule has to follow the record rather than assume "left wins".
        using var h = await LoadAsync("[\"needle\",\"b\",\"c\"]", "[\"b\",\"c\",\"needle\"]");

        await h.Controller.FindAsync("needle", 1);

        Assert.NotNull(h.Vm.SelectedPosition);
        var row = h.SelectedRow();
        Assert.Equal("\"needle\"", (row.Left ?? row.Right)!.Value);
    }

    [Fact]
    public async Task Find_NoMatchInEitherDocument_ReportsNoMatches()
    {
        using var h = await LoadAsync("""{"a":1}""", """{"a":2}""");

        await h.Controller.FindAsync("nothinghere", 1);

        Assert.Null(h.Vm.SelectedPosition);
        Assert.Contains("No matches", h.Statuses);
    }
}
