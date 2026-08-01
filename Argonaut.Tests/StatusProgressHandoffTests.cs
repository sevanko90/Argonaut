using System.Threading;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Argonaut.Tests;

/// <summary>
/// Regression: the finished status line reverted from the document's real total ("N tokens")
/// back to "Indexing… (100%)", intermittently, on every view. The scan thread posts its last
/// progress updates just before indexing completes, so one could still be sitting in the
/// dispatcher queue when the document published its final text - and land after it.
///
/// These run on the headless dispatcher (unlike MainWindowViewModelTests, which is deliberately
/// dispatcher-free) because the bug lives entirely in the ordering of posted work: a fake that
/// never drains the queue would pass no matter what the shell did.
/// </summary>
[Collection("AppDataPaths")]
public sealed class StatusProgressHandoffTests : IDisposable
{
    private readonly string tempDir;

    public StatusProgressHandoffTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        AppDataPaths.RootOverride = Path.Combine(tempDir, "settings");
    }

    public void Dispose()
    {
        AppDataPaths.RootOverride = null;
        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private sealed class FakeNavigator : ISearchNavigator
    {
        public MMapFile File => throw new NotSupportedException();
        public void SetHighlightTerm(string? term) { }
        public Task RevealAsync(SearchMatch match, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDocument : ObservableObject, IDocumentViewModel
    {
        private string status = "loaded";

        public string FilePath { get; init; } = string.Empty;

        public string StatusText
        {
            get => status;
            set => SetField(ref status, value);
        }

        public IndexFailure? IndexFailure => null;

        public object? Toolbar => null;

        public TaskCompletionSource Indexing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task IndexingTask => Indexing.Task;

        public ISearchNavigator? CreateSearchNavigator() => new FakeNavigator();

        public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => true;

        public void Dispose() { }
    }

    private string WriteJsonFile()
    {
        string path = Path.Combine(tempDir, "doc.json");
        File.WriteAllText(path, "{\"a\":1}");
        return path;
    }

    [Fact]
    public Task TrailingProgressPost_DoesNotOverwriteTheDocumentsFinalStatus()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StatusProgressHandoffTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteJsonFile();
            var document = new FakeDocument { FilePath = path, StatusText = "12,345 tokens indexed so far" };

            IProgressReporter? reporter = null;
            var vm = new MainWindowViewModel(_ => Task.FromResult(true), (_, _, r) =>
            {
                reporter = r;
                return Task.FromResult<IDocumentViewModel>(document);
            });

            await vm.OpenPathAsync(path);
            Dispatcher.UIThread.RunJobs();

            // The scan's last progress update, posted while indexing was still running.
            reporter!.Report("Indexing", 100, 100);

            // Indexing finishes and the document publishes its real total - exactly the window
            // in which that already-queued post used to land on top of it.
            document.StatusText = "12,345 tokens";
            document.Indexing.SetResult();
            await document.IndexingTask;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("12,345 tokens", vm.StatusText);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public Task ProgressBeforeIndexingCompletes_StillUpdatesTheStatusLine()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StatusProgressHandoffTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteJsonFile();
            var document = new FakeDocument { FilePath = path, StatusText = "250 rows indexed so far" };

            IProgressReporter? reporter = null;
            var vm = new MainWindowViewModel(_ => Task.FromResult(true), (_, _, r) =>
            {
                reporter = r;
                return Task.FromResult<IDocumentViewModel>(document);
            });

            await vm.OpenPathAsync(path);
            Dispatcher.UIThread.RunJobs();

            // Still indexing: live progress is the useful thing to show, so it must win here.
            reporter!.Report("Indexing", 45, 100);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("45%", vm.StatusText);
            return true;
        }, CancellationToken.None);
    }
}
