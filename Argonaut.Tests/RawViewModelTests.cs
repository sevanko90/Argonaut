using System.Text;
using Argonaut.Features.Raw;
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
}
