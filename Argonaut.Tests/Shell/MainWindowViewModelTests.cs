using Argonaut.Tests.Support;
using System.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Search;
using Argonaut.Engine.Selection;
using Argonaut.Engine.Settings;
using Argonaut.Shell;
using Argonaut.Shell.CommandLine;
using Argonaut.Ui.Documents;
using Argonaut.Ui.Find;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Tests.Shell;

/// <summary>
/// Exercises the shell's open/close lifecycle in isolation: the staleness guard and the
/// document-disposal contract (the crash/leak the code-behind's request-id juggling used to
/// guard by hand), status mirroring, and the close semantics. Documents are lightweight
/// fakes injected through the view model's DocumentLoader seam, so no real memory mapping,
/// indexing, or UI dispatcher is involved. Settings live in an in-memory store per test.
/// </summary>
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string tempDir;

    public MainWindowViewModelTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ArgonautTestFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
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
        private IndexFailure? indexFailure;

        public IByteOrigin? Origin { get; init; }

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

    private static MainWindowViewModel CreateViewModel(MainWindowViewModel.DocumentLoader loader, ISettingsStore? settings = null)
        => new(settings ?? SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true), documentLoader: loader);

    /// <summary>The shell logs through the log it is given - here, that a path that does not
    /// exist was turned away.</summary>
    [Fact]
    public async Task OpenPath_MissingFile_IsLoggedToTheGivenLog()
    {
        var log = new RecordingDiagnosticLog();
        var vm = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            documentLoader: (_, _, _) => throw new InvalidOperationException("nothing should load"), log: log);
        string missing = Path.Combine(tempDir, "missing.json");

        await vm.OpenPathAsync(missing);

        Assert.Contains(log.Lines, line => line.Contains("File.Exists false") && line.Contains(missing));
    }

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

        var vm = CreateViewModel((_, origin, _) =>            Task.FromResult<IDocumentViewModel>(origin.Path == pathA ? docA : docB));

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

        var vm = CreateViewModel((_, origin, _) => origin.Path == pathA ? gateA.Task : gateB.Task);

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
        var vm = CreateViewModel((_, origin, _) =>            Task.FromResult<IDocumentViewModel>(origin.Path == pathA ? docA : docB));

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
    public async Task ToggleFavourite_MovesAFileFromRecentToFavouritesAndBack()
    {
        string path = WriteJsonFile();
        var document = new FakeDocument { FilePath = path };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));
        await vm.OpenPathAsync(path);

        vm.ToggleFavourite(path);

        Assert.Contains(vm.Favourites, item => item.IsFavourite && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vm.RecentFiles, item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

        vm.ToggleFavourite(path);

        Assert.Empty(vm.Favourites);
        Assert.Contains(vm.RecentFiles, item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Favourites_SurviveReopeningTheSettings()
    {
        var settings = SettingsStore.InMemory();
        settings.Get<FavouriteFiles>().Toggle(Path.Combine(tempDir, "pinned.ini"));

        var vm = CreateViewModel((_, _, _) => throw new InvalidOperationException(), settings);

        Assert.Equal("pinned.ini", Assert.Single(vm.Favourites).FileName);
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
        var settings = SettingsStore.InMemory();
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(new FakeDocument()), settings);

        Assert.Equal(ContentFontMode.Monospace, vm.ContentFontMode);
        vm.ToggleContentFont();
        Assert.Equal(ContentFontMode.SansSerif, vm.ContentFontMode);
        vm.ToggleContentFont();
        Assert.Equal(ContentFontMode.Monospace, vm.ContentFontMode);

        vm.ToggleContentFont();
        var reloaded = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(new FakeDocument()), settings);
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
        var vm = new MainWindowViewModel(SettingsStore.InMemory(), TestSchemas.Catalog(),
            _ => throw new InvalidOperationException("confirmReplace must not be called for a view switch"),
            documentLoader: (kind, _, _) => Task.FromResult<IDocumentViewModel>(kind == FileTypeDetector.FileKind.Json ? initial : switched));

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
    public async Task OpenPaths_BothJson_EntersDiffMode()
    {
        string left = WriteJsonFile("left.json");
        string right = WriteJsonFile("right.json");
        var vm = CreateViewModel((_, _, _) => throw new Exception("documentLoader must not be called for a diff"));

        await vm.OpenPathsAsync(left, right);

        Assert.IsType<Argonaut.Features.Json.Diff.JsonDiffViewModel>(vm.CurrentDocument);
        Assert.Equal(left, vm.FilePath);
        Assert.Null(vm.SelectedView); // Unknown kind - the view switcher shows no selection
    }

    [Fact]
    public async Task OpenPaths_SecondNotJson_OpensFirstAlone()
    {
        string left = WriteJsonFile("left.json");
        string right = Path.Combine(tempDir, "right.txt");
        File.WriteAllText(right, "not json at all\nplain text\n");

        var document = new FakeDocument { FilePath = left };
        var vm = CreateViewModel((_, path, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathsAsync(left, right);

        Assert.Same(document, vm.CurrentDocument);
        Assert.Equal(left, vm.FilePath);
    }

    [Fact]
    public async Task OpenPaths_SecondMissing_OpensFirstAlone()
    {
        string left = WriteJsonFile("left.json");
        string right = Path.Combine(tempDir, "does-not-exist.json");

        var document = new FakeDocument { FilePath = left };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathsAsync(left, right);

        Assert.Same(document, vm.CurrentDocument);
        Assert.Equal(left, vm.FilePath);
    }

    [Fact]
    public async Task OpenPaths_NoSecond_BehavesLikeOpenPath()
    {
        string left = WriteJsonFile("left.json");
        var document = new FakeDocument { FilePath = left };
        var vm = CreateViewModel((_, _, _) => Task.FromResult<IDocumentViewModel>(document));

        await vm.OpenPathsAsync(left, null);

        Assert.Same(document, vm.CurrentDocument);
        Assert.True(vm.IsFileOpen);
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

    // ---- origin lifetime -----------------------------------------------------------------

    /// <summary>
    /// The load-bearing property of an origin's lifetime being the open INPUT rather than the
    /// view: re-indexing the same document as a different kind must hand the loader the very
    /// same origin. If it built a new one, a URL would be re-downloaded and a paste
    /// re-materialised every time the user switched views.
    /// </summary>
    [Fact]
    public async Task SwitchView_ReusesTheSameOriginRatherThanBuildingANewOne()
    {
        string path = WriteJsonFile();
        var seen = new List<IByteOrigin>();
        var vm = CreateViewModel((_, origin, _) =>
        {
            seen.Add(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin, FilePath = path });
        });

        await vm.OpenPathAsync(path);
        await vm.SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);

        Assert.Equal(2, seen.Count);
        Assert.Same(seen[0], seen[1]);
    }

    [Fact]
    public async Task OpeningADifferentPath_BuildsANewOrigin()
    {
        string pathA = WriteJsonFile("a.json");
        string pathB = WriteJsonFile("b.json");
        var seen = new List<IByteOrigin>();
        var vm = CreateViewModel((_, origin, _) =>
        {
            seen.Add(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin, FilePath = origin.Path! });
        });

        await vm.OpenPathAsync(pathA);
        await vm.OpenPathAsync(pathB);

        Assert.Equal(2, seen.Count);
        Assert.NotSame(seen[0], seen[1]);
        Assert.Equal(Path.GetFullPath(pathA), seen[0].Path);
        Assert.Equal(Path.GetFullPath(pathB), seen[1].Path);
    }

    /// <summary>
    /// The origin reaches the document, so anything that needs to know whether these bytes are a
    /// file on disk (recent files, the remembered schema binding, save, reveal in folder) can ask rather
    /// than inferring it from a display string.
    /// </summary>
    [Fact]
    public async Task ThePublishedDocumentCarriesItsOrigin()
    {
        string path = WriteJsonFile();
        var vm = CreateViewModel((_, origin, _) =>
            Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin, FilePath = path }));

        await vm.OpenPathAsync(path);

        Assert.NotNull(vm.CurrentDocument!.Origin);
        Assert.Equal(Path.GetFullPath(path), vm.CurrentDocument.Origin!.Path);
    }

    // ---- command line ------------------------------------------------------------------

    private string WriteLinesFile(int count, string name = "lines.ndjson")
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, count).Select(i => $"{{\"n\":{i}}}\n")));
        return path;
    }

    private static string ReadAll(IByteOrigin origin)
    {
        var source = origin.Open();
        try
        {
            return source.GetUtf8String(0, (int)source.AvailableLength);
        }
        finally
        {
            source.Release();
        }
    }

    [Fact]
    public async Task OpenExcerpt_LoadsOnlyTheLinesAskedFor()
    {
        string path = WriteLinesFile(10);
        IByteOrigin? loaded = null;
        string? loadedText = null;
        var vm = CreateViewModel((_, origin, _) =>
        {
            loaded = origin;
            loadedText = ReadAll(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "3-4"]));

        var excerpt = Assert.IsType<ExcerptByteOrigin>(loaded);
        Assert.Equal("{\"n\":3}\n{\"n\":4}\n", loadedText);
        Assert.Null(excerpt.Path);
        Assert.Equal(3, excerpt.Lines!.Value.FirstLine);
        Assert.Equal(4, excerpt.Lines!.Value.LastLine);
    }

    [Fact]
    public async Task OpenExcerpt_PastTheEnd_OpensNothing()
    {
        string path = WriteLinesFile(10);
        var vm = CreateViewModel((_, _, _) => throw new InvalidOperationException("nothing should load"));

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "11-20"]));

        Assert.False(vm.IsFileOpen);
    }

    [Fact]
    public async Task OpenStartup_View_ForcesTheKindInsteadOfDetecting()
    {
        string path = WriteJsonFile();
        FileTypeDetector.FileKind? loadedAs = null;
        var vm = CreateViewModel((kind, _, _) =>
        {
            loadedAs = kind;
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { FilePath = path });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--view", "raw"]));

        Assert.Equal(FileTypeDetector.FileKind.Unidentified, loadedAs);
    }

    [Fact]
    public async Task OpenStartup_LinesAndView_ForcesTheKindOfTheExcerpt()
    {
        string path = WriteLinesFile(10);
        FileTypeDetector.FileKind? loadedAs = null;
        var vm = CreateViewModel((kind, origin, _) =>
        {
            loadedAs = kind;
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "2-", "--view", "raw"]));

        Assert.Equal(FileTypeDetector.FileKind.Unidentified, loadedAs);
    }

    [Fact]
    public async Task OpenWholeFile_LeavesTheExcerptForItsFileInTheSameView()
    {
        string path = WriteLinesFile(10);
        var loads = new List<(FileTypeDetector.FileKind Kind, IByteOrigin Origin)>();
        var vm = CreateViewModel((kind, origin, _) =>
        {
            loads.Add((kind, origin));
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "3-4", "--view", "raw"]));
        Assert.NotNull(vm.Excerpt);

        await vm.OpenWholeFileAsync();

        Assert.Null(vm.Excerpt);
        Assert.Equal(2, loads.Count);
        Assert.Equal(FileTypeDetector.FileKind.Unidentified, loads[1].Kind);
        Assert.Equal(Path.GetFullPath(path), loads[1].Origin.Path);
    }

    private string WriteFilter(string text, string name = "picks.txt")
    {
        string path = Path.Combine(tempDir, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>All of <paramref name="origin"/>, gathered - a selection's runs are not
    /// contiguous, so <see cref="ReadAll"/> cannot read one whole.</summary>
    private static string Gather(IByteOrigin origin)
    {
        var source = origin.Open();
        try
        {
            var bytes = new byte[source.AvailableLength];
            source.CopyTo(0, bytes);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            source.Release();
        }
    }

    private static async Task<List<string>> Toasts(Func<Task> open)
    {
        var toasts = new List<string>();
        void Capture(string message) => toasts.Add(message);
        ToastService.Requested += Capture;
        try
        {
            await open();
        }
        finally
        {
            ToastService.Requested -= Capture;
        }

        return toasts;
    }

    [Fact]
    public async Task OpenSelection_LoadsTheListedLinesInFileOrder_ReadOnly()
    {
        string path = WriteLinesFile(10);
        string filter = WriteFilter("7 # last\n2-3\n");
        IByteOrigin? loaded = null;
        string? loadedText = null;
        FileTypeDetector.FileKind? loadedAs = null;
        var vm = CreateViewModel((kind, origin, _) =>
        {
            loaded = origin;
            loadedAs = kind;
            loadedText = Gather(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", filter]));

        var selection = Assert.IsType<SelectionByteOrigin>(loaded);
        Assert.Equal("{\"n\":2}\n{\"n\":3}\n{\"n\":7}\n", loadedText);
        Assert.True(selection.IsReadOnly);
        Assert.Null(selection.Path);
        Assert.Equal(FileTypeDetector.FileKind.Ndjson, loadedAs);
        Assert.True(vm.IsExcerpt);
        Assert.Same(selection, vm.Selection);
        Assert.Equal("3 lines selected", vm.ExcerptLabel);
        Assert.Equal("3 lines selected from lines.ndjson, as picks.txt lists them", vm.ExcerptDescription);
        Assert.Equal("lines.ndjson", vm.HeaderTitle);
    }

    [Fact]
    public async Task OpenSelection_OfAJsonFile_OpensAsRawText()
    {
        string path = Path.Combine(tempDir, "pretty.json");
        File.WriteAllText(path, "{\n  \"a\": 1,\n  \"b\": 2\n}\n");
        FileTypeDetector.FileKind? loadedAs = null;
        var vm = CreateViewModel((kind, origin, _) =>
        {
            loadedAs = kind;
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", WriteFilter("2-3")]));

        Assert.Equal(FileTypeDetector.FileKind.Unidentified, loadedAs);
    }

    [Fact]
    public async Task OpenSelection_ByBytes_TakesTheRangesAsGiven()
    {
        string path = WriteLinesFile(3);
        string? loadedText = null;
        var vm = CreateViewModel((_, origin, _) =>
        {
            loadedText = Gather(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--bytes", "--filter", WriteFilter("0-7, 16-22")]));

        Assert.Equal("{\"n\":1}\n{\"n\":3}\n", loadedText);
        Assert.Equal("2 ranges selected", vm.ExcerptLabel);
    }

    [Theory]
    [InlineData("1-2\nnope\n", "\"picks.txt\" Line 2: \"nope\" is not a line range.")]
    [InlineData("# nothing yet\n", "\"picks.txt\" lists no lines.")]
    [InlineData("11-20", "Nothing picks.txt lists is in \"lines.ndjson\".")]
    public async Task OpenSelection_ThatSelectsNothing_OpensNothing_AndSaysWhy(string filter, string toast)
    {
        string path = WriteLinesFile(10);
        string filterPath = WriteFilter(filter);
        var vm = CreateViewModel((_, _, _) => throw new InvalidOperationException("nothing should load"));

        var toasts = await Toasts(() => vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", filterPath])));

        Assert.False(vm.IsFileOpen);
        Assert.Equal([toast], toasts);
    }

    [Fact]
    public async Task OpenSelection_MissingFilterFile_SaysSo()
    {
        string path = WriteLinesFile(10);
        var vm = CreateViewModel((_, _, _) => throw new InvalidOperationException("nothing should load"));

        var toasts = await Toasts(() => vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", Path.Combine(tempDir, "gone.txt")])));

        Assert.Equal(["The filter file \"gone.txt\" doesn't exist."], toasts);
    }

    [Fact]
    public async Task OpenSelection_RangesPastTheEnd_AreDropped_AndCounted()
    {
        string path = WriteLinesFile(10);
        var vm = CreateViewModel((_, origin, _) => Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin }));

        var toasts = await Toasts(() => vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", WriteFilter("1, 20, 30-")])));

        Assert.NotNull(vm.Selection);
        Assert.Equal(["2 ranges in picks.txt are past the end of \"lines.ndjson\"."], toasts);
    }

    /// <summary>Filter files held in memory, whose change the test announces.</summary>
    private sealed class FakeFilterFiles : ISelectionFilterFiles
    {
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

        public Action? Changed { get; private set; }

        public int Watching { get; private set; }

        public SelectionFilter Read(string path, SelectionUnit unit)
            => Text.TryGetValue(path, out var text)
                ? SelectionFilter.Parse(new StringReader(text), unit)
                : throw new FileNotFoundException(path);

        public IDisposable Watch(string path, Action changed)
        {
            Changed = changed;
            Watching++;
            return new Unwatch(this);
        }

        private sealed class Unwatch(FakeFilterFiles owner) : IDisposable
        {
            public void Dispose() => owner.Watching--;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);

        Assert.True(condition());
    }

    /// <summary>
    /// Waits for the reload a filter change started to replace <paramref name="shown"/> with the
    /// selection it found - not for the reload's load: the shell calls the loader before it adopts
    /// what was loaded, and the reload runs on past the test's <c>Changed</c> call, so a test that
    /// waited for the load could read the excerpt label while it still described the old selection.
    /// </summary>
    private static Task WaitForReload(MainWindowViewModel vm, SelectionByteOrigin shown)
        => WaitUntil(() => vm.Selection is { } selection && !ReferenceEquals(selection, shown));

    private MainWindowViewModel CreateFollowingViewModel(FakeFilterFiles filters, List<string> loaded)
        => new(SettingsStore.InMemory(), TestSchemas.Catalog(), _ => Task.FromResult(true),
            documentLoader: (_, origin, _) =>
            {
                loaded.Add(Gather(origin));
                return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
            },
            filterFiles: filters);

    [Fact]
    public async Task OpenSelection_FollowsItsFilterFile_AsItChanges()
    {
        string path = WriteLinesFile(10);
        string filterPath = Path.Combine(tempDir, "picks.txt");
        var filters = new FakeFilterFiles();
        filters.Text[filterPath] = "2";
        var loaded = new List<string>();
        var vm = CreateFollowingViewModel(filters, loaded);

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", filterPath]));
        Assert.Equal(["{\"n\":2}\n"], loaded);
        Assert.Equal(1, filters.Watching);

        var shown = vm.Selection!;
        filters.Text[filterPath] = "2\n9";
        filters.Changed!();
        await WaitForReload(vm, shown);

        Assert.Equal("{\"n\":2}\n{\"n\":9}\n", loaded[1]);
        Assert.Equal("2 lines selected", vm.ExcerptLabel);
        Assert.Equal(1, filters.Watching);
    }

    [Fact]
    public async Task OpenSelection_KeepsWhatItShows_WhenTheFilterStopsReading()
    {
        string path = WriteLinesFile(10);
        string filterPath = Path.Combine(tempDir, "picks.txt");
        var filters = new FakeFilterFiles();
        filters.Text[filterPath] = "2-3";
        var loaded = new List<string>();
        var vm = CreateFollowingViewModel(filters, loaded);
        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", filterPath]));
        var shown = vm.Selection;

        filters.Text[filterPath] = "2-3\noops";
        var toasts = await Toasts(async () =>
        {
            filters.Changed!();
            await Task.Delay(50);
        });

        Assert.Single(loaded);
        Assert.Same(shown, vm.Selection);
        Assert.Equal(["\"picks.txt\" Line 2: \"oops\" is not a line range. Still showing what it listed before."], toasts);
    }

    [Fact]
    public async Task OpenSelection_ThatAChangeEmpties_StaysOpen_Empty()
    {
        string path = WriteLinesFile(10);
        string filterPath = Path.Combine(tempDir, "picks.txt");
        var filters = new FakeFilterFiles();
        filters.Text[filterPath] = "4";
        var loaded = new List<string>();
        var vm = CreateFollowingViewModel(filters, loaded);
        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", filterPath]));

        var shown = vm.Selection!;
        filters.Text[filterPath] = "# none for now";
        filters.Changed!();
        await WaitForReload(vm, shown);

        Assert.Equal("", loaded[1]);
        Assert.Equal("0 lines selected", vm.ExcerptLabel);
        Assert.Equal(1, filters.Watching);
    }

    [Fact]
    public async Task OpenSelection_StopsFollowing_WhenSomethingElseIsOpened()
    {
        string path = WriteLinesFile(10);
        string filterPath = Path.Combine(tempDir, "picks.txt");
        var filters = new FakeFilterFiles();
        filters.Text[filterPath] = "4";
        var loaded = new List<string>();
        var vm = CreateFollowingViewModel(filters, loaded);
        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", filterPath]));

        await vm.OpenWholeFileAsync();

        Assert.Equal(0, filters.Watching);
    }

    [Fact]
    public async Task OpenWholeFile_LeavesTheSelectionForItsFile()
    {
        string path = WriteLinesFile(10);
        var loads = new List<IByteOrigin>();
        var vm = CreateViewModel((_, origin, _) =>
        {
            loads.Add(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--lines", "--filter", WriteFilter("3, 8")]));
        await vm.OpenWholeFileAsync();

        Assert.Null(vm.Selection);
        Assert.False(vm.IsExcerpt);
        Assert.Equal(Path.GetFullPath(path), loads[^1].Path);
    }

    [Fact]
    public async Task OpenByteExcerpt_LoadsExactlyTheBytesAskedFor()
    {
        string path = WriteLinesFile(10);
        string? loadedText = null;
        IByteOrigin? loaded = null;
        var vm = CreateViewModel((_, origin, _) =>
        {
            loaded = origin;
            loadedText = ReadAll(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--bytes", "2-6"]));

        var excerpt = Assert.IsType<ExcerptByteOrigin>(loaded);
        Assert.Equal("n\":1}", loadedText);
        Assert.Null(excerpt.Lines);
        Assert.Equal(new ByteRange(2, 5), excerpt.Range);
    }

    [Fact]
    public async Task OpenByteExcerpt_StartingInsideACharacter_StartsAtTheCharacter()
    {
        string path = Path.Combine(tempDir, "accents.txt");
        File.WriteAllText(path, "ab€cd"); // € is three bytes, at 2..4
        string? loadedText = null;
        var vm = CreateViewModel((_, origin, _) =>
        {
            loadedText = ReadAll(origin);
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--bytes", "4-"]));

        Assert.Equal("€cd", loadedText);
    }

    [Fact]
    public async Task OpenByteExcerpt_PastTheEnd_OpensNothing_AndAnEndPastIt_IsCutShort()
    {
        string path = WriteLinesFile(2); // 16 bytes
        var loaded = new List<string>();
        var vm = CreateViewModel((_, origin, _) =>
        {
            loaded.Add(ReadAll(origin));
            return Task.FromResult<IDocumentViewModel>(new FakeDocument { Origin = origin });
        });

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--bytes", "16-20"]));
        Assert.Empty(loaded);

        await vm.OpenStartupAsync(StartupArguments.Parse([path, "--bytes", "8+100"]));
        Assert.Equal(["{\"n\":2}\n"], loaded);
    }

    /// <summary>Opens <paramref name="path"/> while another program holds it locked against
    /// readers, and returns the toasts the attempt raised.</summary>
    private static async Task<List<string>> ToastsOpeningLocked(string path, Func<Task> open)
    {
        var toasts = new List<string>();
        void Capture(string message) => toasts.Add(message);
        ToastService.Requested += Capture;
        try
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await open();
        }
        finally
        {
            ToastService.Requested -= Capture;
        }

        return toasts;
    }

    [Fact]
    public async Task OpenPath_FileLockedByAnotherProgram_SaysSo()
    {
        string path = WriteJsonFile();
        var vm = CreateViewModel((_, _, _) => throw new InvalidOperationException("nothing should load"));

        var toasts = await ToastsOpeningLocked(path, () => vm.OpenPathAsync(path));

        Assert.Contains(toasts, toast => toast.Contains("doc.json"));
        Assert.False(vm.IsFileOpen);
    }

    [Theory]
    [InlineData("--lines", "2-3")]
    [InlineData("--bytes", "0-9")]
    public async Task OpenExcerpt_FileLockedByAnotherProgram_SaysSo(string option, string range)
    {
        string path = WriteLinesFile(10);
        var vm = CreateViewModel((_, _, _) => throw new InvalidOperationException("nothing should load"));

        var toasts = await ToastsOpeningLocked(path, () => vm.OpenStartupAsync(StartupArguments.Parse([path, option, range])));

        Assert.Contains(toasts, toast => toast.Contains("lines.ndjson"));
        Assert.False(vm.IsFileOpen);
    }
}
