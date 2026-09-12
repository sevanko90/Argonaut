using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Exercises the raw document view model's load, wrap-width change, and lifetime contracts.
/// The wrap-width change is the interesting one: it must re-index over the SAME mapping (a
/// live search scan may hold spans over it) while swapping the rows collection instance, so
/// the outgoing ListBox walk reads nothing. AppDataPaths.RootOverride redirects the wrap-width
/// preference store to a temp dir so the developer's real settings are never touched.
/// </summary>
[Collection("AppDataPaths")]
public sealed class RawViewModelTests : IDisposable
{
    private readonly string settingsRoot;
    private readonly string tempDir;

    public RawViewModelTests()
    {
        settingsRoot = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
        AppDataPaths.RootOverride = settingsRoot;

        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        AppDataPaths.RootOverride = null;
        TryDelete(settingsRoot);
        TryDelete(tempDir);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private string WriteFile(byte[] content, string name = "data.bin")
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>400 x's, no newline: 3 rows at wrap 160, 5 rows at wrap 80.</summary>
    private string WriteNewlinelessFile()
    {
        var content = new byte[400];
        Array.Fill(content, (byte)'x');
        return WriteFile(content);
    }

    [Fact]
    public async Task LoadAsync_UsesTheDefaultWrapWidth_AndIndexesTheFile()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            Assert.Equal(RawWrapWidthPreference.Default, vm.WrapWidth);
            Assert.Equal(3, vm.RowCount);
            Assert.NotNull(vm.Toolbar);
            Assert.Contains("3 rows", vm.StatusText);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task LoadAsync_HonorsTheSavedWrapWidth()
    {
        RawWrapWidthPreference.Save(80);

        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            Assert.Equal(80, vm.WrapWidth);
            Assert.Equal(5, vm.RowCount);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SetWrapWidth_ReindexesOverTheSameMapping_AndSwapsTheRowsInstance()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            var mmapBefore = vm.Mmap;
            var rowsBefore = vm.Rows;
            var indexBefore = vm.Index;
            int generationBefore = vm.IndexGeneration;

            vm.SetWrapWidth(80);
            await vm.IndexingTask;

            Assert.Same(mmapBefore, vm.Mmap);          // the mapping must survive (live search safety)
            Assert.NotSame(rowsBefore, vm.Rows);       // fresh ItemsSource instance
            Assert.NotSame(indexBefore, vm.Index);
            Assert.Equal(generationBefore + 1, vm.IndexGeneration);
            Assert.Equal(80, vm.WrapWidth);
            Assert.Equal(5, vm.RowCount);
            Assert.Null(vm.SelectedRowIndex);

            // The retired collection reports empty for Avalonia's trailing ItemsSource walk.
            Assert.Empty(rowsBefore);
            Assert.Null(rowsBefore[0]);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Blocks a search scan mid-window until released - see FileSearchSessionTests'
    /// identical BlockingMatcher for why this makes the interleaving deterministic.</summary>
    private sealed class BlockingMatcher : ISearchMatcher
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public int ChunkOverlap => 0;

        public bool TryFindNext(ReadOnlySpan<byte> chunk, int from, out int matchIndex, out int matchLength)
        {
            Entered.Set();
            Release.Wait();
            matchIndex = -1;
            matchLength = 0;
            return false;
        }
    }

    /// <summary>
    /// SetWrapWidth's promise: "a running search is unaffected - matches are byte offsets over
    /// the unchanged file". Now structural rather than a matter of which token the scan took -
    /// the scan reads its own mappings of the path and shares nothing with the index being
    /// restarted - so this asserts the guarantee end to end, without any lifetime plumbing.
    /// </summary>
    [Fact]
    public async Task SetWrapWidth_DoesNotAffectARunningSearch()
    {
        var vm = new RawViewModel();
        var matcher = new BlockingMatcher();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            var session = FileSearchSession.Start(new ScanTarget(vm.FilePath), matcher);

            matcher.Entered.Wait(); // the scan is now provably mid-chunk

            vm.SetWrapWidth(80); // must re-index without cancelling the search above
            await vm.IndexingTask;

            Assert.False(session.ScanTask.IsCompleted); // still blocked - RestartIndex left it alone
            Assert.Equal(5, vm.RowCount); // the re-index itself completed normally

            matcher.Release.Set();
            await session.ScanTask;
            Assert.False(session.WasCancelled); // ran to natural completion, never cancelled by the wrap change
        }
        finally
        {
            matcher.Release.Set(); // idempotent - unblocks the scan even if an assertion above failed
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SetWrapWidth_WithTheCurrentWidth_IsANoOp()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            var rowsBefore = vm.Rows;
            vm.SetWrapWidth(vm.WrapWidth);

            Assert.Same(rowsBefore, vm.Rows);
            Assert.Equal(0, vm.IndexGeneration);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ToolbarComboChange_AppliesAndPersistsTheWrapWidth()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            var toolbar = Assert.IsType<RawToolbarViewModel>(vm.Toolbar);
            toolbar.WrapWidthIndex = 0; // 80 bytes
            await vm.IndexingTask;

            Assert.Equal(80, vm.WrapWidth);
            Assert.Equal(5, vm.RowCount);
            Assert.Equal(80, RawWrapWidthPreference.Load());
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SelectRow_UpdatesSelectedRowIndex()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            vm.SelectRow(2);

            Assert.Equal(2, vm.SelectedRowIndex);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task JumpToByteOffsetAsync_SelectsTheRowContainingThatOffset()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile()); // wrap 160 by default -> rows at 0,160,320
            await vm.IndexingTask;

            await vm.JumpToByteOffsetAsync(165); // 5 bytes into the second row

            Assert.Equal(1, vm.SelectedRowIndex);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// A jump moves the caret too, not just the viewport. Scrolling somewhere without moving the
    /// caret leaves the next keystroke acting on wherever it was last - which, after a jump
    /// across a multi-GB file, is nowhere near what the user is now looking at.
    /// </summary>
    [Fact]
    public async Task JumpToByteOffsetAsync_PutsTheCaretOnThatOffset()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            await vm.JumpToByteOffsetAsync(165);

            Assert.NotNull(vm.Caret);
            Assert.Equal(165, vm.Caret!.Caret.Offset);
            Assert.True(vm.Caret.Selection.IsEmpty);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task JumpToByteOffsetAsync_AfterDispose_DoesNotThrow()
    {
        var vm = new RawViewModel();
        await vm.LoadAsync(WriteNewlinelessFile());
        await vm.IndexingTask;

        vm.Dispose();

        // The mapping is gone; resolving against it must be swallowed, not surfaced as an
        // unhandled ObjectDisposedException from a fire-and-forget caller.
        await vm.JumpToByteOffsetAsync(10);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndMakesSetWrapWidthANoOp()
    {
        var vm = new RawViewModel();
        await vm.LoadAsync(WriteNewlinelessFile());
        await vm.IndexingTask;

        vm.Dispose();
        vm.Dispose();

        vm.SetWrapWidth(512); // must not touch the disposed session
        Assert.Equal(RawWrapWidthPreference.Default, vm.WrapWidth);
    }

    [Fact]
    public async Task RepeatedWrapChangesWithReveals_DoNotGrowTheManagedHeap()
    {
        // ~6MB, 60k lines: big enough that a leaked index/collection generation (~1MB+ of
        // anchors, caches and strings per cycle) is visible above the slack threshold.
        var content = new byte[6 * 1024 * 1024];
        Array.Fill(content, (byte)'x');
        for (int i = 100; i < content.Length; i += 100)
            content[i] = (byte)'\n';
        string path = WriteFile(content);

        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(path);
            await vm.IndexingTask;

            long baseline = 0;
            for (int cycle = 0; cycle < 6; cycle++)
            {
                vm.SetWrapWidth(cycle % 2 == 0 ? 80 : 512);
                // A reveal racing the re-index - the field scenario - must not pin anything.
                var reveal = RawOffsetRowResolver.ResolveWhenCoveredAsync(vm.Index!, content.Length - 10, CancellationToken.None);
                await vm.IndexingTask;
                Assert.NotNull(await reveal);

                // Realize some rows so caches/LRU churn like a live view.
                for (int i = 0; i < 200; i++)
                    _ = vm.Rows[i];

                long heap = GC.GetTotalMemory(forceFullCollection: true);
                if (cycle == 1)
                    baseline = heap; // cycle 0 warms statics/pools
                else if (cycle > 1)
                    Assert.True(heap < baseline + 4_000_000,
                        $"managed heap grew from {baseline:N0} to {heap:N0} by cycle {cycle} - a generation is being retained");
            }
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RowContent_ExposesLineNumbersAndWrapMarkers()
    {
        // Line 1 wraps once at 160; line 2 fits.
        var content = new byte[204];
        Array.Fill(content, (byte)'x');
        content[200] = (byte)'\n';
        content[201] = (byte)'a';
        content[202] = (byte)'b';
        content[203] = (byte)'\n';

        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteFile(content));
            await vm.IndexingTask;

            Assert.Equal(3, vm.RowCount);

            var row0 = Assert.IsType<RawVisibleRow>(vm.Rows[0]);
            Assert.Equal(1, row0.LineNumber);
            Assert.True(row0.IsSoftWrapped);
            Assert.Equal(new string('x', 160), row0.Text);

            var row1 = Assert.IsType<RawVisibleRow>(vm.Rows[1]);
            Assert.Null(row1.LineNumber);
            Assert.False(row1.IsSoftWrapped);
            Assert.Equal(new string('x', 40), row1.Text);

            var row2 = Assert.IsType<RawVisibleRow>(vm.Rows[2]);
            Assert.Equal(2, row2.LineNumber);
            Assert.Equal("ab", row2.Text);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// The status gutter's text comes from the view model, and follows the caret: moving it is
    /// what the gutter exists to report on.
    /// </summary>
    [Fact]
    public async Task CaretReadout_FollowsTheCaret()
    {
        string path = WriteFile(Encoding.UTF8.GetBytes("abc\ndef\u2028ghi\n"));
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(path);
            await vm.IndexingTask;

            Assert.NotNull(vm.Caret);
            vm.Caret!.PlaceAt(0);

            Assert.Equal("U+0061 LATIN SMALL LETTER A", vm.CaretCharacterText);
            Assert.Equal("Byte 0    Ln 1, Col 1", vm.CaretPositionText);
            Assert.Equal(string.Empty, vm.CaretSelectionText);

            // Onto the separator: three bytes into line 2, and named from the file rather than
            // from the glyph the row draws for it.
            vm.Caret.PlaceAt(7);

            Assert.Equal("U+2028 LINE SEPARATOR", vm.CaretCharacterText);
            Assert.Equal("Byte 7    Ln 2, Col 4", vm.CaretPositionText);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task CaretReadout_ReportsSelectionInBytesAndCharacters()
    {
        string path = WriteFile(Encoding.UTF8.GetBytes("a\u65e5\u672c\u8a9eb\n"));
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(path);
            await vm.IndexingTask;

            Assert.NotNull(vm.Caret);
            vm.Caret!.PlaceAt(1);
            vm.Caret.ExtendTo(10);

            Assert.Equal("Selected 9 bytes (3 chars)", vm.CaretSelectionText);

            vm.Caret.ClearSelection();
            Assert.Equal(string.Empty, vm.CaretSelectionText);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// A wrap-width change replaces the caret controller. The gutter has to end up subscribed to
    /// the new one, or it freezes on whatever it last said.
    /// </summary>
    [Fact]
    public async Task CaretReadout_SurvivesAWrapWidthChange()
    {
        var vm = new RawViewModel();
        try
        {
            await vm.LoadAsync(WriteNewlinelessFile());
            await vm.IndexingTask;

            vm.SetWrapWidth(80);
            await vm.IndexingTask;

            Assert.NotNull(vm.Caret);
            vm.Caret!.PlaceAt(100);

            Assert.Equal("U+0078 LATIN SMALL LETTER X", vm.CaretCharacterText);
            Assert.Equal("Byte 100    Ln 1, Col 101", vm.CaretPositionText);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
