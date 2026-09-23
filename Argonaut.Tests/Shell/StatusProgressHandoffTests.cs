using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Progress;
using Argonaut.Engine.Search;
using Argonaut.Engine.Settings;
using Argonaut.Shell;
using Argonaut.Tests.Support;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;
using Argonaut.Ui.Progress;
using Argonaut.Ui.ViewModels;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Argonaut.Tests.Shell;

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
        public ScanTarget ScanTarget => throw new NotSupportedException();
        public void SetHighlightTerm(string? term) { }
        public Task RevealAsync(SearchMatch match, CancellationToken ct) => Task.CompletedTask;

        /// <summary>No session behind this fake, so nothing ever tears down. Stated explicitly
        /// because ISearchNavigator deliberately gives this member no default - see its remarks.</summary>
        public CancellationToken DocumentTearingDown => default;
    }

    private sealed class FakeDocument : ObservableObject, IDocumentViewModel
    {
        private string status = "loaded";

        public IByteOrigin? Origin { get; init; }

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
            var vm = new MainWindowViewModel(_ => Task.FromResult(true), documentLoader: (_, _, r) =>
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

    /// <summary>Progress goes to the load's entry on the progress board, not the status line -
    /// which keeps the document's own text throughout.</summary>
    [Fact]
    public Task ProgressDuringALoad_GoesToTheProgressBoard()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StatusProgressHandoffTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = WriteJsonFile();
            var document = new FakeDocument { FilePath = path, StatusText = "250 rows indexed so far" };
            var board = new ProgressBoard(TimeProvider.System);

            IProgressReporter? reporter = null;
            var vm = new MainWindowViewModel(_ => Task.FromResult(true), documentLoader: (_, _, r) =>
            {
                reporter = r;
                return Task.FromResult<IDocumentViewModel>(document);
            }, progressBoard: board);

            await vm.OpenPathAsync(path);
            reporter!.Report("Indexing", 45, 100);
            Dispatcher.UIThread.RunJobs();

            var entry = Assert.IsType<ProgressEntry>(reporter);
            Assert.Equal(45, entry.Percent);
            Assert.Equal("Indexing doc.json", entry.Title);
            Assert.True(entry.CanStop);
            Assert.Equal("250 rows indexed so far", vm.StatusText);

            // Indexing ending finishes the entry.
            document.Indexing.SetResult();
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.True(entry.IsFinished);
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// A wrap-width change re-indexes the whole file after the load's own progress has finished,
    /// so the document reports it itself - otherwise a large file shows one stale "rows indexed
    /// so far" with no sign of progress until the total appears.
    /// </summary>
    [Fact]
    public Task WrapWidthChange_ReportsReindexProgress_ThenTheFinalTotal()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StatusProgressHandoffTests).Assembly);
        return session.Dispatch(async () =>
        {
            string path = Path.Combine(tempDir, "big.txt");
            var line = new string('x', 99) + "\n";
            File.WriteAllText(path, string.Concat(Enumerable.Repeat(line, 400_000))); // 40MB, several scan chunks

            var board = new ProgressBoard(TimeProvider.System);
            var vm = new Argonaut.Features.Raw.RawViewModel(board);
            try
            {
                await vm.LoadAsync(new FileByteOrigin(path));
                await vm.IndexingTask;

                var percents = new List<int>();
                ProgressEntry? begun = null;
                board.WorkStarted += (_, entry) => begun = entry;
                vm.SetWrapWidth(vm.WrapWidth == 80 ? 160 : 80);

                var reindex = begun!;
                reindex.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ProgressEntry.Percent) && reindex.Percent is int p)
                        percents.Add(p);
                };

                while (!vm.IndexingTask.IsCompleted)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(5);
                }

                try { await vm.IndexingTask; } catch { }
                for (int i = 0; i < 5; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(5);
                }

                Assert.Equal("Re-indexing big.txt", reindex.Title);
                Assert.NotEmpty(percents);
                Assert.True(reindex.IsFinished);
                Assert.EndsWith("rows", vm.StatusText);
            }
            finally
            {
                vm.Dispose();
            }

            return true;
        }, CancellationToken.None);
    }
}
