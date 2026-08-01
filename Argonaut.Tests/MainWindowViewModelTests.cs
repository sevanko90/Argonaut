using System.Threading;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;

namespace Argonaut.Tests;

/// <summary>
/// Exercises the shell's open/close lifecycle in isolation: the staleness guard and the
/// document-disposal contract (the crash/leak the code-behind's request-id juggling used to
/// guard by hand), status mirroring, and the close semantics. Documents are lightweight
/// fakes injected through the view model's DocumentLoader seam, so no real memory mapping,
/// indexing, or UI dispatcher is involved. AppDataPaths.RootOverride redirects the recent-file
/// and preference stores to a temp dir so the developer's real settings are never touched.
/// </summary>
[Collection("AppDataPaths")]
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string settingsRoot;
    private readonly string tempDir;

    public MainWindowViewModelTests()
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

    /// <summary>Writes a valid single-line JSON file so FileTypeDetector classifies it (the
    /// fake loader ignores the detected kind, but OpenPathAsync still runs detection).</summary>
    private string WriteJsonFile(string name = "doc.json")
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllText(path, "{\"a\":1}");
        return path;
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
        private IndexFailure? indexFailure;

        public string FilePath { get; init; } = string.Empty;

        public string StatusText
        {
            get => status;
            set => SetField(ref status, value);
        }

        public IndexFailure? IndexFailure
        {
            get => indexFailure;
            set => SetField(ref indexFailure, value);
        }

        public bool HasSearchNavigator { get; init; } = true;

        public bool Disposed { get; private set; }

        /// <summary>Lets a test hold indexing "running" and finish it on demand, to drive the
        /// shell's hand-back of the status line (see IDocumentViewModel.IndexingTask). No
        /// RunContinuationsAsynchronously, so SetResult runs the shell's continuation inline and
        /// the test needs no polling to observe the hand-back.</summary>
        public TaskCompletionSource Indexing { get; } = new();

        public Task IndexingTask => Indexing.Task;

        public object? Toolbar { get; init; }

        public ISearchNavigator? CreateSearchNavigator() => HasSearchNavigator ? new FakeNavigator() : null;

        /// <summary>
        /// Returns true if the VM can process the specified file type
        /// </summary>
        /// <param name="fileType">Type of file to query</param>
        /// <returns>True if the view model can process the specified file type</returns>
        public bool CanHandleFileType(FileTypeDetector.FileKind fileType)
        {
            return true; // fake, can handle anything
        }

        public void Dispose() => Disposed = true;
    }

    private static MainWindowViewModel CreateViewModel(MainWindowViewModel.DocumentLoader loader)
        => new(_ => Task.FromResult(true), loader);

    [Fact]
    public async Task OpenPath_PublishesDocument_AndMirrorsStatus()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path, StatusText = "42 rows" };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathAsync(path);

        Assert.Same(document, vm.CurrentDocument);
        Assert.True(vm.IsFileOpen);
        Assert.Equal(path, vm.FilePath);
        Assert.Equal(Path.GetFileName(path), vm.FileName);
        Assert.Equal("Argonaut — " + Path.GetFileName(path), vm.Title);
        Assert.Equal("42 rows", vm.StatusText);
    }

    [Fact]
    public async Task OpenPath_UnidentifiedFile_RoutesToTheLoaderAndPublishes()
    {
        // Plain prose: no JSON start token and no delimiters, so detection yields
        // Unidentified - which must now reach the loader (raw viewer) instead of being
        // silently abandoned.
        string path = Path.Combine(tempDir, "notes.txt");
        File.WriteAllText(path, "hello world\nno structure here\n");

        FileTypeDetector.FileKind? seenKind = null;
        var document = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((kind, _, _) =>
        {
            seenKind = kind;
            return Task.FromResult<IDocumentViewModel>(document);
        });

        await vm.OpenPathAsync(path);

        Assert.Equal(FileTypeDetector.FileKind.Unidentified, seenKind);
        Assert.Same(document, vm.CurrentDocument);
        Assert.True(vm.IsFileOpen);
    }

    [Fact]
    public async Task DocumentStatusChange_UpdatesShellStatus()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));
        await vm.OpenPathAsync(path);

        document.StatusText = "indexing failed";

        Assert.Equal("indexing failed", vm.StatusText);
    }

    [Fact]
    public async Task StatusMirror_StopsAfterDocumentReplaced()
    {
        string pathA = WriteJsonFile("a.json");
        string pathB = WriteJsonFile("b.json");
        var docA = new FakeDocument { FilePath = pathA };
        var docB = new FakeDocument { FilePath = pathB, StatusText = "B loaded" };

        var vm = CreateViewModel((_, path, _) =>
            Task.FromResult<IDocumentViewModel>(path == pathA ? docA : docB));

        await vm.OpenPathAsync(pathA);
        await vm.OpenPathAsync(pathB);

        // The outgoing document is no longer mirrored once replaced.
        docA.StatusText = "stale update";
        Assert.Equal("B loaded", vm.StatusText);
    }

    [Fact]
    public async Task StaleOpen_DisposesLoser_AndKeepsWinner()
    {
        string pathA = WriteJsonFile("a.json");
        string pathB = WriteJsonFile("b.json");
        var docA = new FakeDocument { FilePath = pathA };
        var docB = new FakeDocument { FilePath = pathB };
        var gateA = new TaskCompletionSource<IDocumentViewModel>();
        var gateB = new TaskCompletionSource<IDocumentViewModel>();

        var vm = CreateViewModel((_, path, _) => path == pathA ? gateA.Task : gateB.Task);

        // Both opens suspend at the loader await; the second bumps the request id, so the
        // first is now stale even though its load finishes last.
        var openA = vm.OpenPathAsync(pathA);
        var openB = vm.OpenPathAsync(pathB);

        gateB.SetResult(docB);
        await openB;
        gateA.SetResult(docA);
        await openA;

        Assert.True(docA.Disposed, "the superseded document must be disposed (else its mapping leaks)");
        Assert.False(docB.Disposed);
        Assert.Same(docB, vm.CurrentDocument);
    }

    [Fact]
    public async Task CloseFile_DisposesDocument_BeforeSwap_AndResetsState()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));
        await vm.OpenPathAsync(path);

        await vm.CloseFileAsync();

        // The shell disposes the outgoing document before clearing CurrentDocument, so its
        // mmap-backed collections are already empty when Avalonia's swap walks them.
        Assert.True(document.Disposed);
        Assert.Null(vm.CurrentDocument);
        Assert.False(vm.IsFileOpen);
        Assert.Null(vm.FilePath);
        Assert.Equal("No file loaded", vm.StatusText);
    }

    [Fact]
    public async Task OpenSecondFile_DisposesOutgoingDocument()
    {
        string pathA = WriteJsonFile("a.json");
        string pathB = WriteJsonFile("b.json");
        var docA = new FakeDocument { FilePath = pathA };
        var docB = new FakeDocument { FilePath = pathB };
        var vm = CreateViewModel((_, p, _) =>
            Task.FromResult<IDocumentViewModel>(p == pathA ? docA : docB));

        await vm.OpenPathAsync(pathA);
        await vm.OpenPathAsync(pathB);

        Assert.True(docA.Disposed, "the replaced document must be disposed before the swap");
        Assert.False(docB.Disposed);
        Assert.Same(docB, vm.CurrentDocument);
    }

    [Fact]
    public async Task FailedLoad_LeavesNoDocument_AndReportsFailure()
    {
        string path = WriteJsonFile();
        var vm = CreateViewModel((_, _, _) =>
            Task.FromException<IDocumentViewModel>(new InvalidDataException("boom")));

        await vm.OpenPathAsync(path);

        Assert.Null(vm.CurrentDocument);
        Assert.False(vm.IsFileOpen);
        Assert.Contains("failed to open", vm.StatusText);
    }

    [Fact]
    public async Task OpenPath_AddsToRecentFiles()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathAsync(path);

        Assert.Contains(vm.RecentFiles, item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenPath_NonexistentFile_IsIgnored()
    {
        var vm = CreateViewModel((_, _, _) => throw new Exception("loader must not be called"));

        await vm.OpenPathAsync(Path.Combine(tempDir, "does-not-exist.json"));

        Assert.Null(vm.CurrentDocument);
        Assert.False(vm.IsFileOpen);
    }

    [Fact]
    public async Task OpenPath_PublishesDocumentToolbar()
    {
        string path = WriteJsonFile();
        var toolbar = new object();
        var document = new FakeDocument { FilePath = path, Toolbar = toolbar };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathAsync(path);

        Assert.Same(toolbar, vm.CurrentDocument?.Toolbar);
    }

    [Fact]
    public void ToggleTheme_CyclesSystemLightDark()
    {
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(new FakeDocument()));

        Assert.Equal(ThemeMode.System, vm.ThemeMode);
        vm.ToggleTheme();
        Assert.Equal(ThemeMode.Light, vm.ThemeMode);
        vm.ToggleTheme();
        Assert.Equal(ThemeMode.Dark, vm.ThemeMode);
        vm.ToggleTheme();
        Assert.Equal(ThemeMode.System, vm.ThemeMode);
    }

    [Fact]
    public void ToggleContentFont_TogglesAndPersists()
    {
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(new FakeDocument()));

        Assert.Equal(ContentFontMode.Monospace, vm.ContentFontMode);
        vm.ToggleContentFont();
        Assert.Equal(ContentFontMode.SansSerif, vm.ContentFontMode);
        vm.ToggleContentFont();
        Assert.Equal(ContentFontMode.Monospace, vm.ContentFontMode);

        vm.ToggleContentFont();
        var reloaded = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(new FakeDocument()));
        Assert.Equal(ContentFontMode.SansSerif, reloaded.ContentFontMode);
    }

    [Fact]
    public async Task OpenPath_SelectedView_TracksDetectedKind()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathAsync(path);

        Assert.Equal(FileTypeDetector.FileKind.Json, vm.SelectedView?.Kind);
    }

    [Fact]
    public async Task OpenPath_DocumentWithNavigator_MakesFindAvailable()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathAsync(path);

        Assert.True(vm.IsFindAvailable);
    }

    [Fact]
    public async Task OpenPath_DocumentWithNoNavigator_LeavesFindUnavailable()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path, HasSearchNavigator = false };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathAsync(path);

        Assert.False(vm.IsFindAvailable);
    }

    [Fact]
    public async Task SwitchView_NeverCallsConfirmReplace()
    {
        string path = WriteJsonFile();
        var initial = new FakeDocument { FilePath = path };
        var switched = new FakeDocument { FilePath = path, StatusText = "switched" };
        var vm = new MainWindowViewModel(
            _ => throw new InvalidOperationException("confirmReplace must not be called for a view switch"),
            (kind, _, _) => Task.FromResult<IDocumentViewModel>(kind == FileTypeDetector.FileKind.Json ? initial : switched));

        await vm.OpenPathAsync(path);
        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Ndjson);

        Assert.Same(switched, vm.CurrentDocument);
    }

    [Fact]
    public async Task SwitchView_DisposesOutgoingDocument_AndDoesNotDuplicateRecentEntry()
    {
        string path = WriteJsonFile();
        var initial = new FakeDocument { FilePath = path };
        var switched = new FakeDocument { FilePath = path, StatusText = "switched" };
        var vm = CreateViewModel((kind, _, _) =>
            Task.FromResult<IDocumentViewModel>(kind == FileTypeDetector.FileKind.Json ? initial : switched));

        await vm.OpenPathAsync(path);
        int recentCountAfterOpen = vm.RecentFiles.Count;

        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Ndjson);

        Assert.True(initial.Disposed, "the outgoing document must be disposed before the swap");
        Assert.False(switched.Disposed);
        Assert.Same(switched, vm.CurrentDocument);
        Assert.Equal(recentCountAfterOpen, vm.RecentFiles.Count);
    }

    [Fact]
    public async Task SwitchView_PreflightRejection_NeverInvokesLoader()
    {
        // Plain prose fails the Json pre-flight check (no leading '{'/'['), so switching to
        // Json must reject before ever reaching the loader.
        string path = Path.Combine(tempDir, "notes.txt");
        File.WriteAllText(path, "hello world\nno structure here\n");

        var document = new FakeDocument { FilePath = path };
        bool loaderCalledForJson = false;
        var vm = CreateViewModel((kind, _, _) =>
        {
            if (kind == FileTypeDetector.FileKind.Json)
                loaderCalledForJson = true;
            return Task.FromResult<IDocumentViewModel>(document);
        });

        await vm.OpenPathAsync(path); // detected Unidentified

        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Json);

        Assert.False(loaderCalledForJson);
        Assert.IsType<IncompatibleViewModel>(vm.CurrentDocument);
        Assert.True(vm.IsFileOpen);
    }

    [Fact]
    public async Task SwitchView_ZeroProgressFailure_ShowsIncompatible_AndDisposesFailedDocument()
    {
        string path = WriteJsonFile();
        var initial = new FakeDocument { FilePath = path };
        var failed = new FakeDocument { FilePath = path, IndexFailure = new IndexFailure("boom", null, 1, 1, 0) };
        var vm = CreateViewModel((kind, _, _) =>
            Task.FromResult<IDocumentViewModel>(kind == FileTypeDetector.FileKind.Json ? initial : failed));

        await vm.OpenPathAsync(path);
        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Ndjson);

        Assert.True(failed.Disposed, "a zero-progress failure must not be left as the published document");
        Assert.IsType<IncompatibleViewModel>(vm.CurrentDocument);
        Assert.True(vm.IsFileOpen);
        Assert.False(vm.IsFailureBannerVisible);
    }

    [Fact]
    public async Task SwitchView_FailureWithItems_PublishesDocument_AndShowsBanner()
    {
        string path = WriteJsonFile();
        var initial = new FakeDocument { FilePath = path };
        var partial = new FakeDocument { FilePath = path, IndexFailure = new IndexFailure("boom", 100, 5, 3, 42) };
        var vm = CreateViewModel((kind, _, _) =>
            Task.FromResult<IDocumentViewModel>(kind == FileTypeDetector.FileKind.Json ? initial : partial));

        await vm.OpenPathAsync(path);
        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Ndjson);

        Assert.Same(partial, vm.CurrentDocument);
        Assert.False(partial.Disposed);
        Assert.True(vm.IsFailureBannerVisible);
        Assert.True(vm.CanJumpToFailureLocation);
        Assert.Equal("Line 5, column 3 (byte 100)", vm.FailureLocationText);

        // The banner has no dismiss affordance - it stays up for as long as the document does.
        partial.StatusText = "still showing partial results";
        Assert.True(vm.IsFailureBannerVisible);
    }

    [Fact]
    public async Task JumpToFailureLocation_SwitchesToRawView()
    {
        string path = WriteJsonFile();
        var initial = new FakeDocument { FilePath = path };
        var raw = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((kind, _, _) =>
            Task.FromResult<IDocumentViewModel>(kind == FileTypeDetector.FileKind.Json ? initial : raw));

        await vm.OpenPathAsync(path);
        await vm.JumpToRawOffsetAsync(12345);

        Assert.Same(raw, vm.CurrentDocument);
        Assert.Equal(FileTypeDetector.FileKind.Unidentified, vm.SelectedView?.Kind);
    }

    [Fact]
    public async Task SwitchView_SameKind_IsNoOp()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path };
        bool loaderCalledTwice = false;
        int loadCount = 0;
        var vm = CreateViewModel((_, _, _) =>
        {
            loadCount++;
            if (loadCount > 1)
                loaderCalledTwice = true;
            return Task.FromResult<IDocumentViewModel>(document);
        });

        await vm.OpenPathAsync(path);
        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Json);

        Assert.False(loaderCalledTwice);
        Assert.Same(document, vm.CurrentDocument);
        Assert.False(document.Disposed);
    }
}
