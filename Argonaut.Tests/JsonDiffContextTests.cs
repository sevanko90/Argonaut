using System.Text;
using Argonaut.Features.Json.Diff;

namespace Argonaut.Tests;

/// <summary>
/// The diff UX layer added on top of v1: the path-vs-value highlight split, next/previous
/// change navigation, and the source/target context bar with its character-level diff.
/// </summary>
public class JsonDiffContextTests
{
    private static string WriteTemp(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static async Task<(JsonDiffViewModel Vm, string LeftPath, string RightPath)> LoadAsync(string leftJson, string rightJson)
    {
        string leftPath = WriteTemp(leftJson);
        string rightPath = WriteTemp(rightJson);
        var vm = new JsonDiffViewModel();
        await vm.LoadAsync(leftPath, rightPath);
        try { await vm.IndexingTask; } catch { }

        // The collection's final rebuild is driven by the growth monitor in the app; in
        // dispatcher-free tests, poll the diff-complete state and rebuild via the filter
        // round-trip (ChangesOnly toggle forces a rebuild without changing semantics).
        vm.Rows.ChangesOnly = true;
        vm.Rows.ChangesOnly = false;
        return (vm, leftPath, rightPath);
    }

    private static List<JsonDiffRow> Materialize(JsonDiffRowCollection rows)
    {
        var list = new List<JsonDiffRow>();
        for (int i = 0; i < rows.Count; i++)
            list.Add((JsonDiffRow)rows[i]!);
        return list;
    }

    private static void Cleanup(JsonDiffViewModel vm, string leftPath, string rightPath)
    {
        vm.Dispose();
        File.Delete(leftPath);
        File.Delete(rightPath);
    }

    // ── Path vs value highlight ────────────────────────────────────────────────────────

    [Fact]
    public async Task DescendedContainer_IsChangedPath_LeafIsValueChanged()
    {
        var (vm, l, r) = await LoadAsync(
            """{"outer":{"v":1,"w":2}}""",
            """{"outer":{"v":9,"w":2}}""");
        try
        {
            var rows = Materialize(vm.Rows);

            var containers = rows.Where(x => x.IsChangedPath).ToList();
            Assert.Equal(2, containers.Count); // root + "outer", the path to the change
            Assert.All(containers, c => Assert.False(c.IsValueChanged));

            var leaf = Assert.Single(rows, x => x.IsValueChanged);
            Assert.Equal("v", leaf.Left!.Name);
            Assert.False(leaf.IsChangedPath);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    // ── Next / previous diff ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GoToNextDiff_SkipsPathContainersAndUnchanged()
    {
        var (vm, l, r) = await LoadAsync(
            """{"a":1,"nested":{"changed":2},"gone":3}""",
            """{"a":1,"nested":{"changed":9}}""");
        try
        {
            var rows = Materialize(vm.Rows);

            vm.GoToNextDiff();
            var first = rows[vm.SelectedPosition!.Value];
            Assert.True(first.IsValueChanged); // the "changed" leaf, not the containers above it

            vm.GoToNextDiff();
            var second = rows[vm.SelectedPosition!.Value];
            Assert.Equal(DiffStatus.Removed, second.Status);

            // Wraps around back to the first change.
            vm.GoToNextDiff();
            Assert.True(rows[vm.SelectedPosition!.Value].IsValueChanged);

            // And previous walks the same ring backwards.
            vm.GoToPreviousDiff();
            Assert.Equal(DiffStatus.Removed, rows[vm.SelectedPosition!.Value].Status);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task GoToNextDiff_IdenticalDocuments_NoSelection()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1}""", """{"a":1}""");
        try
        {
            vm.GoToNextDiff();
            Assert.Null(vm.SelectedPosition);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    // ── Context bar ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectingModifiedLeaf_ContextShowsCharacterDiff()
    {
        var (vm, l, r) = await LoadAsync(
            """{"url":"https://example.com/v1/users"}""",
            """{"url":"https://example.com/v2/users"}""");
        try
        {
            vm.GoToNextDiff();

            Assert.True(vm.HasSelection);
            Assert.Equal("\"https://example.com/v", vm.SourcePrefix);
            Assert.Equal("1", vm.SourceChanged);
            Assert.Equal("/users\"", vm.SourceSuffix);
            Assert.Equal("2", vm.TargetChanged);
            Assert.Equal(vm.SourcePrefix, vm.TargetPrefix);
            Assert.Equal(vm.SourceSuffix, vm.TargetSuffix);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task SwappingToPathMode_ShowsJsonPath()
    {
        var (vm, l, r) = await LoadAsync(
            """{"nested":{"v":1}}""",
            """{"nested":{"v":2}}""");
        try
        {
            vm.GoToNextDiff();
            Assert.Equal("path", vm.SourceModeLabel);

            vm.ToggleSourceMode();
            Assert.Equal("value", vm.SourceModeLabel);
            Assert.Equal("$.nested.v", vm.SourcePrefix);
            Assert.Equal(string.Empty, vm.SourceChanged);

            // Target row is independent and still shows its value.
            Assert.Equal("2", vm.TargetChanged);

            vm.ToggleSourceMode();
            Assert.Equal("1", vm.SourceChanged);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task AddedRow_TargetValueFullyHighlighted_SourceEmpty()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1}""", """{"a":1,"new":"hello"}""");
        try
        {
            vm.GoToNextDiff();

            Assert.Equal(string.Empty, vm.SourcePrefix + vm.SourceChanged + vm.SourceSuffix);
            Assert.Equal(string.Empty, vm.TargetPrefix);
            Assert.Equal("\"hello\"", vm.TargetChanged);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task MovedRow_SingleSideShownPlain_NotHighlighted()
    {
        var (vm, l, r) = await LoadAsync("[1,2,3,4]", "[3,2,1,4]");
        try
        {
            vm.GoToNextDiff();
            var row = Materialize(vm.Rows)[vm.SelectedPosition!.Value];
            Assert.Equal(DiffStatus.Moved, row.Status);

            // Content is unchanged by definition - no highlight run on either line.
            Assert.Equal(string.Empty, vm.SourceChanged);
            Assert.Equal(string.Empty, vm.TargetChanged);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task ClearingSelection_ClearsContext()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1}""", """{"a":2}""");
        try
        {
            vm.GoToNextDiff();
            Assert.True(vm.HasSelection);

            vm.SelectedPosition = null;
            Assert.False(vm.HasSelection);
            Assert.Equal(string.Empty, vm.SourcePrefix + vm.SourceChanged + vm.SourceSuffix);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task MirroredSubRow_UnderIndexShiftedAncestor_TargetPathUsesRightContainerIndex()
    {
        // {"id":1,"nested":[3,4]} is byte-identical on both sides, so the array aligner
        // matches it as a single unique-hash anchor - but "gone"/"new" around it mean its
        // own array index shifts from items[1] (left) to items[0] (right). It's Unchanged
        // and undescended (no diff sub-records), so expanding it - and then its "nested"
        // child - token-walks the LEFT document into both panes (mirrored). The target path
        // for a leaf under it must reflect the RIGHT document's items[0], not items[1].
        var (vm, l, r) = await LoadAsync(
            """{"items":["gone",{"id":1,"nested":[3,4]}]}""",
            """{"items":[{"id":1,"nested":[3,4]},"new"]}""");
        try
        {
            var rows = Materialize(vm.Rows);
            var anchor = Assert.Single(rows, x => x.Status == DiffStatus.Unchanged && x.HasChildren);

            vm.Rows.ToggleExpand(anchor.Position);
            rows = Materialize(vm.Rows);
            var nested = Assert.Single(rows, x => x.Left is { Name: "nested" });

            vm.Rows.ToggleExpand(nested.Position);
            rows = Materialize(vm.Rows);
            var three = Assert.Single(rows, x => x.Left is { Value: "3" });
            Assert.Same(three.Left, three.Right); // mirrored, per the walk's own contract

            vm.SelectedPosition = three.Position;
            vm.ToggleTargetMode();

            Assert.Equal("$.items[0].nested[0]", vm.TargetPrefix);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    // ── The affix split itself ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("abc", "abd", "ab", "c", "")]
    [InlineData("abc", "xbc", "", "a", "bc")]
    [InlineData("same", "same", "same", "", "")]
    [InlineData("\"port\": 5432", "\"port\": 9999", "\"port\": ", "5432", "")]
    [InlineData("", "x", "", "", "")]
    [InlineData("abba", "aba", "ab", "b", "a")]
    public void SplitByCommonAffixes_BracketsTheDifference(string value, string other, string prefix, string changed, string suffix)
    {
        var (p, c, s) = JsonDiffViewModel.SplitByCommonAffixes(value, other);
        Assert.Equal(prefix, p);
        Assert.Equal(changed, c);
        Assert.Equal(suffix, s);
    }
}
