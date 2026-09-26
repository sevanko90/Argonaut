using System.Text;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Diff;
using Argonaut.Tests.Support;
using Argonaut.Ui.Find;

namespace Argonaut.Tests.Features.Json.Diff;

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

        public JsonDiffTree Tree => Vm.DiffTree!;

        public List<TreeRow> Rows() => JsonDiffRows.All(Tree);

        public TreeRow SelectedRow() => Vm.SelectedRow ?? throw new InvalidOperationException("Nothing selected:\n" + Describe(this));

        public string? Name(TreeRow row) => JsonDiffRows.Name(Tree, row);

        public string? Value(TreeRow row, bool leftSide) => JsonDiffRows.Value(Tree, row, leftSide);

        public void Dispose()
        {
            // The scans hold spans over the mappings the view model is about to dispose.
            Controller.Detach();
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

        // Same wait JsonDiffContextTests uses: the growth monitor's final rebuild resumes on a
        // pool thread with no dispatcher installed, so the scan's own task is not enough to
        // know the rows have settled. See IndexGrowthMonitor.FinalRefreshTask.
        await vm.FinalRefreshTask;

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

        var row = h.SelectedRow();
        Assert.Null(JsonDiffRows.Detail(row).Right);
        Assert.Equal("onlyleft", h.Name(row));
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

        var row = h.SelectedRow();
        Assert.Null(JsonDiffRows.Detail(row).Left);
        Assert.Equal("onlyright", h.Name(row));
    }

    [Fact]
    public async Task Find_HighlightTermIsPushedIntoTheDocument_AndClearedOnStop()
    {
        using var h = await LoadAsync("""{"a":"needle"}""", """{"a":"needle"}""");

        await h.Controller.FindAsync("needle", 1);
        Assert.Equal("needle", h.Vm.HighlightTerm);

        h.Controller.StopSearch();
        Assert.Null(h.Vm.HighlightTerm);
    }

    /// <summary>The final count lands once both scans finish, which is a beat after the press.</summary>
    private static async Task AssertSettlesOnAsync(Harness h, string expected)
    {
        for (int i = 0; i < 200 && !h.Statuses.Contains(expected); i++)
            await Task.Delay(10);

        Assert.Contains(expected, h.Statuses);
    }

    /// <summary>Waits until the status has reported a total, whatever position it is at - i.e.
    /// until both files have been scanned to the end and the ring of stops is whole.</summary>
    private static async Task SettleOnStopCountAsync(Harness h, int stops)
    {
        string total = $"of {stops} rows";
        bool Reported() => h.Statuses.Exists(s => s is not null && s.EndsWith(total, StringComparison.Ordinal));

        for (int i = 0; i < 200 && !Reported(); i++)
            await Task.Delay(10);

        Assert.True(Reported(), $"No status reported {stops} stops; last was '{(h.Statuses.Count > 0 ? h.Statuses[^1] : null)}'.");
    }

    /// <summary>What the document actually held, for an assertion that is about to fail on a
    /// timing-dependent state - the row list and every status the find reported.</summary>
    private static string Describe(Harness h)
        => $"status='{h.Vm.StatusText}' failure='{h.Vm.IndexFailure?.Message}'\n"
            + JsonDiffRows.Describe(h.Tree)
            + "\nstatuses=" + string.Join(" | ", h.Statuses);

    /// <summary>The stop's name, whichever side of the row carries it.</summary>
    private static string Label(Harness h, TreeRow row) => h.Name(row)!;

    [Fact]
    public async Task Find_BothPanesOfOneRow_AreASingleStop()
    {
        // "needle" on each side of a Modified leaf: two occurrences, but one row. Find stops
        // there once, so pressing next always moves rather than appearing to do nothing.
        using var h = await LoadAsync(
            """{"a":"needle-left"}""",
            """{"a":"needle-right"}""");

        await h.Controller.FindAsync("needle", 1);

        await AssertSettlesOnAsync(h, "1 of 1 rows");
    }

    [Fact]
    public async Task Find_SeveralOccurrencesInOneRow_AreASingleStop()
    {
        // The name and the value of the same property both match. Still one row, one stop.
        using var h = await LoadAsync(
            """{"gone":"a gone value","x":1}""",
            """{"x":2}""");

        await h.Controller.FindAsync("gone", 1);

        await AssertSettlesOnAsync(h, "1 of 1 rows");
    }

    [Fact]
    public async Task Find_CountsPlacesItWillStop_NotTimesTheBytesOccur()
    {
        // The heart of the stop list: "needle" occurs in BOTH files, but the unchanged "outer"
        // member is drawn from the left document into both panes, so the right file's copy
        // is not on screen and find will never stop there. The count has to say 1, not 2 -
        // otherwise it advertises a stop that cannot be reached and the numbering skips.
        using var h = await LoadAsync(
            """{"outer":{"deep":"needle"},"x":1}""",
            """{"outer":{"deep":"needle"},"x":2}""");

        await h.Controller.FindAsync("needle", 1);

        await AssertSettlesOnAsync(h, "1 of 1 rows");
    }

    [Fact]
    public async Task Find_StepsThroughBothDocumentsInMergedOrder_ThenWraps()
    {
        // Three stops interleaved across the two files - removed "aaa", added "mmm", removed
        // "zzz" - so walking them in merged order is a different sequence from draining either
        // document first. Two stops could not show that: a two-element ring is the same cycle
        // whichever order it was built in.
        using var h = await LoadAsync(
            """{"aaa":"needle","keep":1,"zzz":"needle"}""",
            """{"mmm":"needle","keep":1}""");

        // Which stop the FIRST press lands on is deliberately not asserted. The two files are
        // scanned independently and a press stops on whatever has been found by the time it is
        // made - that is the point of being able to search a file still being scanned, and the
        // right file's scan legitimately wins that race sometimes. Merged order is a promise
        // about the ring, and the ring is only whole once both scans have reported: settle
        // first, then walk it.
        await h.Controller.FindAsync("needle", 1);
        await SettleOnStopCountAsync(h, 3);

        string[] merged = ["aaa", "mmm", "zzz"];

        var walked = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            await h.Controller.FindAsync("needle", 1);
            walked.Add(Label(h, h.SelectedRow()));
        }

        // Four steps over three stops, so this also proves the wrap: whichever stop the walk
        // started from, it must run the merged order from there and come round again.
        int start = Array.IndexOf(merged, walked[0]);
        Assert.InRange(start, 0, merged.Length - 1);
        for (int i = 0; i < walked.Count; i++)
            Assert.Equal(merged[(start + i) % merged.Length], walked[i]);

        // And previous walks the same ring backwards.
        for (int i = 1; i <= merged.Length; i++)
        {
            await h.Controller.FindAsync("needle", -1);
            int expected = ((start + walked.Count - 1 - i) % merged.Length + merged.Length) % merged.Length;
            Assert.Equal(merged[expected], Label(h, h.SelectedRow()));
        }
    }

    [Fact]
    public async Task Find_RevealsAMatchHiddenInsideACollapsedContainer()
    {
        using var h = await LoadAsync(
            """{"outer":{"inner":{"deep":"needle"}},"x":1}""",
            """{"outer":{"inner":{"deep":"needle"}},"x":2}""");

        // The unchanged "outer" is collapsed in a run - the match has no row at all until find
        // opens the way to it.
        Assert.DoesNotContain(h.Rows(), r => h.Value(r, leftSide: true) == "\"needle\"");

        await h.Controller.FindAsync("needle", 1);

        var row = h.SelectedRow();
        Assert.Equal("deep", h.Name(row));
        Assert.Equal("\"needle\"", h.Value(row, leftSide: true));
    }

    [Fact]
    public async Task Find_MirroredRegion_TheRightDocumentCopyIsNotASecondStop()
    {
        // "outer" is unchanged, so it is drawn from the LEFT document into both panes - the right
        // file's bytes there are never rendered. Both files match
        // "needle", but only one of those is on screen, so find must offer exactly one stop.
        // Before suppression the right copy became a second stop that fell back to the record
        // row ABOVE the real one, so find-next appeared to jump backwards.
        using var h = await LoadAsync(
            """{"outer":{"deep":"needle"},"x":1}""",
            """{"outer":{"deep":"needle"},"x":2}""");

        await h.Controller.FindAsync("needle", 1);
        long first = h.SelectedRow().Start;
        Assert.Equal("deep", h.Name(h.SelectedRow()));

        // Stepping on wraps straight back to the same single stop - it never lands on the
        // enclosing "outer" row.
        await h.Controller.FindAsync("needle", 1);
        Assert.Equal(first, h.SelectedRow().Start);

        await h.Controller.FindAsync("needle", -1);
        Assert.Equal(first, h.SelectedRow().Start);
    }

    [Fact]
    public async Task Find_RightOnlyTextOnAModifiedLeaf_IsStillReachable()
    {
        // Guards against over-suppressing: a Modified record's own row renders BOTH panes, so
        // text that exists only in the right document there is genuinely on screen and must
        // still be findable.
        using var h = await LoadAsync("""{"a":"cat"}""", """{"a":"zebra"}""");

        await h.Controller.FindAsync("zebra", 1);

        Assert.Equal("\"zebra\"", h.Value(h.SelectedRow(), leftSide: false));
    }

    [Fact]
    public async Task Find_MovedSubtree_IsReachableAtTheEndThatRendersIt()
    {
        // A move renders its content at ONE end (the destination shows the right document), so
        // the suppression rule has to follow the record rather than assume "left wins".
        using var h = await LoadAsync("[\"needle\",\"b\",\"c\"]", "[\"b\",\"c\",\"needle\"]");

        await h.Controller.FindAsync("needle", 1);

        var row = h.SelectedRow();
        Assert.Equal("\"needle\"", h.Value(row, leftSide: true) ?? h.Value(row, leftSide: false));
    }

    [Fact]
    public async Task Find_CrossParentMove_StopsAtBothEnds()
    {
        // Both records of a cross-parent move carry both documents' nodes, but each end draws
        // only one: the stub at the source the left document, the destination the right. A match
        // on either side must be keyed to the end that draws it, or it is never a stop.
        using var h = await LoadAsync(
            """{"config":{"db":{"host":"needle"}},"meta":{}}""",
            """{"config":{},"meta":{"db":{"host":"needle"}}}""");

        await h.Controller.FindAsync("needle", 1);

        await AssertSettlesOnAsync(h, "1 of 2 rows");
        Assert.Equal("\"needle\"", h.Value(h.SelectedRow(), leftSide: true) ?? h.Value(h.SelectedRow(), leftSide: false));
    }

    [Fact]
    public async Task Find_NoMatchInEitherDocument_ReportsNoMatches()
    {
        using var h = await LoadAsync("""{"a":1}""", """{"a":2}""");

        await h.Controller.FindAsync("nothinghere", 1);

        Assert.Null(h.Vm.SelectedRow);
        Assert.Contains("No matches", h.Statuses);
    }
}
