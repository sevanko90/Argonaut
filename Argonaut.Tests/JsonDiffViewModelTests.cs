using System.Text;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Tests;

/// <summary>
/// The diff document's IDocumentViewModel contract: a two-file search navigator (one find
/// bar over both documents), no claimed file kind (never offered by the switcher), a
/// change-count summary once the diff completes, and idempotent disposal from both the
/// shell and the view detach path.
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

    [Fact]
    public async Task Load_CompletesDiff_AndSummarizesChanges()
    {
        string leftPath = WriteTemp("""{"a":1,"gone":2}""");
        string rightPath = WriteTemp("""{"a":9,"new":3}""");
        var vm = new JsonDiffViewModel();
        try
        {
            await vm.LoadAsync(leftPath, rightPath);
            try { await vm.IndexingTask; } catch { }

            // MonitorAsync's continuation races the test; poll for its status write.
            await WaitForAsync(() => vm.StatusText.Contains("added"));

            Assert.Contains("1 added", vm.StatusText);
            Assert.Contains("1 removed", vm.StatusText);
            Assert.Contains("1 modified", vm.StatusText);
            Assert.True(vm.Rows.Count > 0);
            Assert.Null(vm.IndexFailure);
        }
        finally
        {
            vm.Dispose();
            File.Delete(leftPath);
            File.Delete(rightPath);
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
            Assert.Equal(2, navigator.Files.Count);
            Assert.Same(navigator.File, navigator.Files[0]);

            foreach (FileTypeDetector.FileKind kind in Enum.GetValues<FileTypeDetector.FileKind>())
                Assert.False(vm.CanHandleFileType(kind));
            Assert.NotNull(vm.Toolbar);
            Assert.Equal(leftPath, vm.FilePath);
        }
        finally
        {
            vm.Dispose();
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task RightSideFailure_AttributedInIndexFailure()
    {
        string leftPath = WriteTemp("""{"ok":1}""");
        string rightPath = WriteTemp("{\"broken\": tru");
        var vm = new JsonDiffViewModel();
        try
        {
            await vm.LoadAsync(leftPath, rightPath);
            try { await vm.IndexingTask; } catch { }

            await WaitForAsync(() => vm.IndexFailure is not null);
            Assert.StartsWith("Right file:", vm.IndexFailure!.Message);
        }
        finally
        {
            vm.Dispose();
            File.Delete(leftPath);
            File.Delete(rightPath);
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
        string leftPath = WriteTemp("""{"a":[1,2,3]}""");
        string rightPath = WriteTemp("""{"a":[1,2,3]}""");
        var vm = new JsonDiffViewModel();
        try
        {
            await vm.LoadAsync(leftPath, rightPath);
            try { await vm.IndexingTask; } catch { }

            await WaitForAsync(() => vm.StatusText.Contains("identical"));
        }
        finally
        {
            vm.Dispose();
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task WindowTitle_NamesBothFiles_AndTheStatusDoesNotRepeatThem()
    {
        string leftPath = WriteTemp("""{"a":1}""");
        string rightPath = WriteTemp("""{"a":2}""");
        var vm = new JsonDiffViewModel();
        try
        {
            await vm.LoadAsync(leftPath, rightPath);
            try { await vm.IndexingTask; } catch { }

            Assert.Equal(
                $"Argonaut Diff ({Path.GetFileName(leftPath)} \u2194 {Path.GetFileName(rightPath)})",
                vm.WindowTitle);

            // The pair identifies the document, so it belongs in the title once - the status
            // bar is left to say what the comparison found.
            await WaitForAsync(() => vm.StatusText.Contains("modified"));
            Assert.DoesNotContain(Path.GetFileName(leftPath), vm.StatusText);
            Assert.DoesNotContain(Path.GetFileName(rightPath), vm.StatusText);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
