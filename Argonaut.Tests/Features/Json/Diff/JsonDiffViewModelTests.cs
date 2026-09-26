using System.Text;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Diff;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Diff;

/// <summary>
/// The diff document: its IDocumentViewModel contract (a two-file search navigator, no claimed
/// file kind, a change-count summary, idempotent disposal), the preview giving way to the merged
/// tree, next/previous change, and the source/target context bar with its character-level diff.
/// </summary>
public class JsonDiffViewModelTests
{
    private static string WriteTemp(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
            await Task.Delay(10);

        Assert.True(condition());
    }

    /// <summary>
    /// Loads and waits for the comparison to settle. With no dispatcher installed the growth
    /// monitor's final refresh - which swaps the preview for the merged tree - resumes on a pool
    /// thread, so the scan's own task is not enough; <c>FinalRefreshTask</c> is.
    /// </summary>
    private static async Task<(JsonDiffViewModel Vm, string LeftPath, string RightPath)> LoadAsync(string leftJson, string rightJson)
    {
        string leftPath = WriteTemp(leftJson);
        string rightPath = WriteTemp(rightJson);
        var vm = new JsonDiffViewModel();
        await vm.LoadAsync(leftPath, rightPath);
        try { await vm.IndexingTask; } catch { }
        await vm.FinalRefreshTask;
        return (vm, leftPath, rightPath);
    }

    private static void Cleanup(JsonDiffViewModel vm, string leftPath, string rightPath)
    {
        vm.Dispose();
        File.Delete(leftPath);
        File.Delete(rightPath);
    }

    private static JsonDiffTree Tree(JsonDiffViewModel vm) => vm.DiffTree!;

    private static TreeRow Selected(JsonDiffViewModel vm) => vm.SelectedRow ?? throw new InvalidOperationException("Nothing selected:\n" + JsonDiffRows.Describe(vm.DiffTree!));

    // ── The document ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_CompletesDiff_AndSummarizesChanges()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1,"gone":2}""", """{"a":9,"new":3}""");
        try
        {
            // MonitorIndexing's continuation races the test; poll for its status write.
            await WaitForAsync(() => vm.StatusText.Contains("added"));

            Assert.Contains("1 added", vm.StatusText);
            Assert.Contains("1 removed", vm.StatusText);
            Assert.Contains("1 modified", vm.StatusText);
            Assert.Same(vm.DiffTree, vm.Tree);
            Assert.NotEmpty(JsonDiffRows.All(Tree(vm)));
            Assert.Null(vm.IndexFailure);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task ShellContract_FindOverBothFilesNoKindOwnToolbar()
    {
        string leftPath = WriteTemp("[1]");
        string rightPath = WriteTemp("[2]");
        var vm = new JsonDiffViewModel();
        try
        {
            await vm.LoadAsync(leftPath, rightPath);

            // Find is offered, and scans BOTH documents through the shell's one find bar.
            var navigator = Assert.IsType<JsonDiffSearchNavigator>(vm.CreateSearchNavigator());
            Assert.Equal(2, navigator.ScanTargets.Count);
            Assert.Equal(navigator.ScanTarget, navigator.ScanTargets[0]);
            Assert.Equal(leftPath, navigator.ScanTargets[0].Origin.Path);
            Assert.Equal(rightPath, navigator.ScanTargets[1].Origin.Path);

            foreach (FileTypeDetector.FileKind kind in Enum.GetValues<FileTypeDetector.FileKind>())
                Assert.False(vm.CanHandleFileType(kind));
            Assert.NotNull(vm.Toolbar);
            Assert.Equal(leftPath, vm.FilePath);
        }
        finally
        {
            Cleanup(vm, leftPath, rightPath);
        }
    }

    [Fact]
    public async Task RightSideFailure_AttributedInIndexFailure()
    {
        var (vm, l, r) = await LoadAsync("""{"ok":1}""", "{\"broken\": tru");
        try
        {
            await WaitForAsync(() => vm.IndexFailure is not null);
            Assert.StartsWith("Right file:", vm.IndexFailure!.Message);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task DoubleDispose_IsANoOp()
    {
        string leftPath = WriteTemp("[1]");
        string rightPath = WriteTemp("[1]");
        var vm = new JsonDiffViewModel();
        try
        {
            await vm.LoadAsync(leftPath, rightPath);
            vm.Dispose();
            vm.Dispose(); // shell dispose + view detach handler
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task IdenticalDocuments_SummarySaysIdentical()
    {
        var (vm, l, r) = await LoadAsync("""{"a":[1,2,3]}""", """{"a":[1,2,3]}""");
        try
        {
            await WaitForAsync(() => vm.StatusText.Contains("identical"));
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task WindowTitle_NamesBothFiles_AndTheStatusDoesNotRepeatThem()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1}""", """{"a":2}""");
        try
        {
            Assert.Equal($"Argonaut Diff ({Path.GetFileName(l)} ↔ {Path.GetFileName(r)})", vm.WindowTitle);

            // The pair identifies the document, so it belongs in the title once - the status
            // bar is left to say what the comparison found.
            await WaitForAsync(() => vm.StatusText.Contains("modified"));
            Assert.DoesNotContain(Path.GetFileName(l), vm.StatusText);
            Assert.DoesNotContain(Path.GetFileName(r), vm.StatusText);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    /// <summary>
    /// The preview of the left document must always give way to the merged tree. Both causes it
    /// once failed to were scheduling races around the diff finishing - completion sampled after
    /// the rows were built, and a test reading the rows while the final refresh replaced them - so
    /// this asserts over enough loads to hit the window.
    /// </summary>
    [Fact]
    public async Task EveryLoad_SettlesOnTheMergedTree_NotThePreview()
    {
        for (int i = 0; i < 200; i++)
        {
            var (vm, l, r) = await LoadAsync("""{"a":1}""", """{"a":1,"new":"hello"}""");
            try
            {
                Assert.True(ReferenceEquals(vm.Tree, vm.DiffTree), $"load {i}: status='{vm.StatusText}'");

                vm.GoToNextDiff();
                var row = Selected(vm);
                Assert.Equal("new", JsonDiffRows.Name(Tree(vm), row));
                Assert.Null(JsonDiffRows.Detail(row).Left);
            }
            finally
            {
                Cleanup(vm, l, r);
            }
        }
    }

    // ── Next / previous change ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GoToNextDiff_SkipsPathContainersAndUnchanged_AndWraps()
    {
        var (vm, l, r) = await LoadAsync(
            """{"a":1,"nested":{"changed":2},"gone":3}""",
            """{"a":1,"nested":{"changed":9}}""");
        try
        {
            vm.GoToNextDiff();
            Assert.Equal("changed", JsonDiffRows.Name(Tree(vm), Selected(vm)));
            Assert.True(JsonDiffRows.Detail(Selected(vm)).IsValueChanged);

            vm.GoToNextDiff();
            Assert.Equal("gone", JsonDiffRows.Name(Tree(vm), Selected(vm)));

            vm.GoToNextDiff();
            Assert.Equal("changed", JsonDiffRows.Name(Tree(vm), Selected(vm)));

            vm.GoToPreviousDiff();
            Assert.Equal("gone", JsonDiffRows.Name(Tree(vm), Selected(vm)));
            Assert.Equal(vm.SelectedRow?.Start, vm.PendingReveal);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task GoToNextDiff_FindsChangeUnderACollapsedAncestor_AndOpensIt()
    {
        var (vm, l, r) = await LoadAsync(
            """{"a":1,"nested":{"changed":2}}""",
            """{"a":1,"nested":{"changed":9}}""");
        try
        {
            var tree = Tree(vm);
            var nested = Assert.Single(JsonDiffRows.All(tree), x => JsonDiffRows.Name(tree, x) == "nested");
            Assert.True(nested.IsExpanded);
            tree.Toggle(nested);
            Assert.DoesNotContain(JsonDiffRows.All(tree), x => JsonDiffRows.Name(tree, x) == "changed");

            vm.GoToNextDiff();

            Assert.Equal("changed", JsonDiffRows.Name(tree, Selected(vm)));
            Assert.True(Assert.Single(JsonDiffRows.All(tree), x => JsonDiffRows.Name(tree, x) == "nested").IsExpanded);
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
            Assert.Null(vm.SelectedRow);
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
        var (vm, l, r) = await LoadAsync("""{"nested":{"v":1}}""", """{"nested":{"v":2}}""");
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
    public async Task AddedRow_TargetValuePlain_SourceShowsAddedPlaceholder()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1}""", """{"a":1,"new":"hello"}""");
        try
        {
            vm.GoToNextDiff();

            // The target is the whole change, not a partial edit against a prior value - plain.
            Assert.Equal("\"hello\"", vm.TargetPrefix);
            Assert.Equal(string.Empty, vm.TargetChanged);
            Assert.Null(vm.TargetPlaceholder);
            Assert.True(vm.ShowTargetValue);

            // The source line has nothing: a placeholder explains why.
            Assert.Equal(string.Empty, vm.SourcePrefix + vm.SourceChanged + vm.SourceSuffix);
            Assert.Contains("added", vm.SourcePlaceholder);
            Assert.False(vm.ShowSourceValue);

            // Swapping to path mode keeps the placeholder rather than a phantom path.
            vm.ToggleSourceMode();
            Assert.Contains("added", vm.SourcePlaceholder);
            vm.ToggleTargetMode();
            Assert.Equal("$.new", vm.TargetPrefix);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task RemovedRow_SourceValuePlain_TargetShowsDeletedPlaceholder()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1,"gone":"bye"}""", """{"a":1}""");
        try
        {
            vm.GoToNextDiff();

            Assert.Equal("\"bye\"", vm.SourcePrefix);
            Assert.Equal(string.Empty, vm.SourceChanged);
            Assert.True(vm.ShowSourceValue);

            Assert.Equal(string.Empty, vm.TargetPrefix + vm.TargetChanged + vm.TargetSuffix);
            Assert.Contains("deleted", vm.TargetPlaceholder);
            Assert.False(vm.ShowTargetValue);

            vm.ToggleTargetMode();
            Assert.Contains("deleted", vm.TargetPlaceholder);
            vm.ToggleSourceMode();
            Assert.Equal("$.gone", vm.SourcePrefix);
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
            var record = Tree(vm).Diff.GetRecord(JsonDiffRows.Detail(Selected(vm)).Record);
            Assert.Equal(DiffStatus.Moved, record.Status);

            // Content is unchanged by definition - no highlight run on either line.
            Assert.Equal(string.Empty, vm.SourceChanged);
            Assert.Equal(string.Empty, vm.TargetChanged);
            Assert.Contains("moved from", vm.SourcePlaceholder);
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

            vm.OnRowSelected(null);
            Assert.False(vm.HasSelection);
            Assert.Equal(string.Empty, vm.SourcePrefix + vm.SourceChanged + vm.SourceSuffix);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task MirroredRow_UnderAnIndexShiftedElement_TargetPathUsesTheRightIndex()
    {
        // The object is identical on both sides but moves from items[1] to items[0]: it is in a
        // run, drawn from the left document into both panes. The target path of a leaf beneath
        // it must name the right document's items[0], not items[1].
        var (vm, l, r) = await LoadAsync(
            """{"items":["gone",{"id":1,"nested":[3,4]}]}""",
            """{"items":[{"id":1,"nested":[3,4]},"new"]}""");
        try
        {
            var tree = Tree(vm);
            var element = Assert.Single(JsonDiffRows.All(tree), x => x.Shape == TreeRowShape.Open && JsonDiffRows.Detail(x).RightMirrorsLeft);
            tree.Toggle(element);
            var nested = Assert.Single(JsonDiffRows.All(tree), x => JsonDiffRows.Name(tree, x) == "nested");
            tree.Toggle(nested);
            var three = Assert.Single(JsonDiffRows.All(tree), x => JsonDiffRows.Value(tree, x, leftSide: true) == "3");
            Assert.True(JsonDiffRows.Detail(three).RightMirrorsLeft);

            vm.OnRowSelected(three);
            vm.ToggleSourceMode();
            vm.ToggleTargetMode();

            Assert.Equal("$.items[1].nested[0]", vm.SourcePrefix);
            Assert.Equal("$.items[0].nested[0]", vm.TargetPrefix);
        }
        finally
        {
            Cleanup(vm, l, r);
        }
    }

    [Fact]
    public async Task ChangesOnly_HidesTheRuns()
    {
        var (vm, l, r) = await LoadAsync("""{"a":1,"b":2}""", """{"a":1,"b":3}""");
        try
        {
            var raised = 0;
            vm.ChangesOnlyChanged += (_, _) => raised++;
            vm.Toolbar!.ChangesOnly = true;

            Assert.Equal(1, raised);
            Assert.DoesNotContain(JsonDiffRows.All(Tree(vm)), x => JsonDiffRows.Name(Tree(vm), x) == "a");
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
