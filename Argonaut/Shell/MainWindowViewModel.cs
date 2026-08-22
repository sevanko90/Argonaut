using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.Raw;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Avalonia.Threading;

namespace Argonaut.Shell;

/// <summary>
/// Shell-level application state and the open/close file lifecycle, factored out of
/// <see cref="MainWindow"/>'s code-behind. Owns the current document view model, the status
/// line, window title, recent-file list, the find controller, and the theme preference. The
/// header's document-specific toolbar (date hints, expand depth) is owned by each document
/// view model instead - see <see cref="IDocumentViewModel.Toolbar"/>.
///
/// All members run on the UI thread; awaits resume there per the app's threading convention
/// (see CLAUDE.md), so the only explicit marshalling is <see cref="StatusProgressReporter"/>,
/// which is invoked from a background indexing/search thread.
///
/// Document disposal follows <see cref="IDocumentViewModel"/>'s lifetime contract: this view
/// model disposes any document it builds that never becomes <see cref="CurrentDocument"/> (a
/// stale open superseded by a newer request, or a failed load); a document that is published
/// is disposed by its hosting view's DetachedFromVisualTree handler when the content swap
/// tears it down.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// Builds the document view model for a detected file kind. Injectable so tests can
    /// supply lightweight fakes in place of the real memory-mapping/indexing view models.
    /// </summary>
    public delegate Task<IDocumentViewModel> DocumentLoader(
        FileTypeDetector.FileKind fileType, string path, IProgressReporter reporter);

    private const string DefaultTitle = AppInfo.Name;

    private readonly Func<string, Task<bool>> confirmReplace;
    private readonly DocumentLoader documentLoader;
    private readonly FindController findController;

    private IDocumentViewModel? currentDocument;
    private string? currentFilePath;
    private FileTypeDetector.FileKind currentKind;
    private string statusText = "No file loaded";
    private string title = DefaultTitle;
    private string fileName = string.Empty;
    private IReadOnlyList<RecentFileItem> recentFiles = Array.Empty<RecentFileItem>();
    private ThemeMode themeMode;
    private ContentFontMode contentFontMode;
    private DocumentViewOption? selectedView;
    private bool isFindAvailable;
    private int openRequestId;

    // The reporter feeding scan progress into the status line for the current load. Held so
    // every path that puts final text on that line can silence it first - see
    // StatusProgressReporter.Stop. Null before the first load.
    private StatusProgressReporter? indexProgressReporter;

    /// <summary>Raised when the find bar's status text should change (null clears it).</summary>
    public event Action<string?>? FindStatusChanged;

    /// <summary>Raised when the find bar should clear its term/status (file open, switch, or close).</summary>
    public event Action? FindBarResetRequested;

    /// <param name="confirmReplace">
    /// Shows the "replace the loaded file?" confirmation and resolves to the user's choice.
    /// Injected so the lifecycle stays view-agnostic and unit-testable.
    /// </param>
    /// <param name="documentLoader">
    /// Overrides how documents are built (defaults to the real memory-mapped view models);
    /// tests inject fakes to exercise the lifecycle without real files or indexing.
    /// </param>
    public MainWindowViewModel(Func<string, Task<bool>> confirmReplace, DocumentLoader? documentLoader = null)
    {
        this.confirmReplace = confirmReplace;
        this.documentLoader = documentLoader ?? DocumentViewCatalog.LoadAsync;

        themeMode = ThemePreference.Load();
        contentFontMode = ContentFontPreference.Load();

        findController = new FindController(
            status => FindStatusChanged?.Invoke(status),
            () => currentFilePath is null ? null : new StatusProgressReporter(this, currentFilePath, openRequestId));

        ReloadRecentFiles();
    }

    public IDocumentViewModel? CurrentDocument
    {
        get => currentDocument;
        private set => SetField(ref currentDocument, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string Title
    {
        get => title;
        private set => SetField(ref title, value);
    }

    /// <summary>True when a document is loaded; drives the toolbar's visibility.</summary>
    public bool IsFileOpen => currentFilePath is not null;

    /// <summary>The current file's name, shown in the toolbar.</summary>
    public string FileName
    {
        get => fileName;
        private set => SetField(ref fileName, value);
    }

    /// <summary>Full path of the current file, shown as the toolbar file-name tooltip.</summary>
    public string? FilePath => currentFilePath;

    /// <summary>All views the user can force onto the current file, for the status-bar switcher.</summary>
    public IReadOnlyList<DocumentViewOption> AvailableViews => DocumentViewCatalog.Options;

    /// <summary>
    /// The switcher's selection: reflects the current document's kind, and forces a view
    /// switch when the user picks a different one. Programmatic updates that merely mirror
    /// the already-current kind (on open, or after a switch completes) are inert - the guard
    /// below only fires <see cref="SwitchViewAsync"/> when the kind actually changed.
    /// </summary>
    public DocumentViewOption? SelectedView
    {
        get => selectedView;
        set
        {
            if (!SetField(ref selectedView, value))
                return;

            if (value is not null && currentFilePath is not null && value.Kind != currentKind)
                _ = SwitchViewAsync(value.Kind);
        }
    }

    /// <summary>
    /// True while the current document is a partial result (some items indexed before a scan
    /// failure) - shows a permanent (non-dismissible - the user must fix or switch away from
    /// the underlying problem) warning banner. False for <see cref="IncompatibleViewModel"/>,
    /// which is itself the full failure display; never both at once since a zero-progress
    /// failure always swaps to that placeholder instead of publishing (see
    /// <see cref="LoadAndPublishAsync"/> / <see cref="OnDocumentPropertyChanged"/>).
    /// </summary>
    public bool IsFailureBannerVisible => currentDocument is not null and not IncompatibleViewModel && currentDocument.IndexFailure is not null;

    /// <summary>See <see cref="IndexFailureFormatting.DescribeLocation"/> for the current
    /// document's failure, or null when there is none (or nothing to show).</summary>
    public string? FailureLocationText => currentDocument?.IndexFailure is { } f ? IndexFailureFormatting.DescribeLocation(f) : null;

    /// <summary>Whether the failure banner's location can be jumped to in the raw viewer (needs
    /// a byte offset - see <see cref="JumpToFailureLocationAsync"/>).</summary>
    public bool CanJumpToFailureLocation => currentDocument?.IndexFailure?.ByteOffset is not null;

    /// <summary>True when the current document has something searchable - false for the
    /// incompatible-file placeholder, which hides the find bar entirely.</summary>
    public bool IsFindAvailable
    {
        get => isFindAvailable;
        private set => SetField(ref isFindAvailable, value);
    }

    /// <summary>
    /// Switches to the raw viewer (if not already showing it) and jumps to
    /// <paramref name="byteOffset"/> - the shell-mediated action behind every failure
    /// location's "Line N" link (the JSON banner's and the incompatible placeholder's alike)
    /// and behind <see cref="RawJumpService"/> requests (e.g. JsonView's "view in raw" link
    /// on a truncated value). Concrete-type match on <see cref="RawViewModel"/> because "jump
    /// to an offset" is meaningful for exactly one view - every other document kind would have
    /// to implement it as a no-op - so it stays off <see cref="IDocumentViewModel"/>, whose job
    /// is the surface *every* document genuinely shares. This is the shell's only such match:
    /// per-view state and behaviour otherwise reach their view through the document's own
    /// injected <see cref="IDocumentViewModel.Toolbar"/>, never through a shell type-switch
    /// (see docs/architecture.md).
    /// </summary>
    public async Task JumpToRawOffsetAsync(long byteOffset)
    {
        if (currentKind != FileTypeDetector.FileKind.Unidentified)
            await SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);

        if (CurrentDocument is RawViewModel raw)
            await raw.JumpToByteOffsetAsync(byteOffset);
    }

    public IReadOnlyList<RecentFileItem> RecentFiles
    {
        get => recentFiles;
        private set => SetField(ref recentFiles, value);
    }

    /// <summary>True when "Compare with…" applies: the current document is being viewed as
    /// JSON. The semantic diff is a JSON-tree feature; other kinds have no tree to align.</summary>
    public bool CanCompare => currentKind == FileTypeDetector.FileKind.Json;

    /// <summary>
    /// Diffs the currently open file (left) against <paramref name="rightPath"/> (right) via
    /// <see cref="OpenDiffAsync"/>. Entered explicitly through the "Compare with…" menu, so
    /// unlike <see cref="OpenPathsAsync"/> it trusts the user's pick instead of running
    /// <see cref="FileTypeDetector"/> on it - requires a document already open to serve as
    /// the left side.
    /// </summary>
    public async Task CompareWithAsync(string rightPath)
    {
        if (currentFilePath is null || string.IsNullOrWhiteSpace(rightPath))
            return;

        var normalizedRight = Path.GetFullPath(rightPath);
        if (!File.Exists(normalizedRight))
            return;

        await OpenDiffAsync(currentFilePath, normalizedRight);
    }

    /// <summary>
    /// Diffs <paramref name="leftPath"/> against <paramref name="rightPath"/>, replacing the
    /// current document (if any) with a <see cref="JsonDiffViewModel"/>. Entered explicitly -
    /// never via <see cref="FileTypeDetector"/> - so the published document carries
    /// <see cref="FileTypeDetector.FileKind.Unknown"/>: the view switcher shows no selection
    /// for it, and picking any view there re-indexes the left file as that kind through the
    /// normal switch path, disposing the diff on the way out. Not added to recent files in v1
    /// (a diff is not a reopenable path). Shared core behind <see cref="CompareWithAsync"/>
    /// (which requires a document already open) and <see cref="OpenPathsAsync"/> (the
    /// command-line startup path, which has none yet).
    /// </summary>
    public async Task OpenDiffAsync(string leftPath, string rightPath)
    {
        var requestId = ++openRequestId;

        // Same pre-swap discipline as every open/switch: a live find scan holds spans over
        // the outgoing MMapFile, and the outgoing load's reporter must go quiet first.
        await DetachFindAsync();
        FindBarResetRequested?.Invoke();
        indexProgressReporter?.Stop();
        StatusText = $"Comparing {leftPath} with {rightPath}…";

        var document = new JsonDiffViewModel();
        try
        {
            await document.LoadAsync(leftPath, rightPath);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenDiff: load threw: {ex}");
            document.Dispose();
            if (requestId == openRequestId)
                StatusText = $"{leftPath} — failed to open comparison";
            return;
        }

        if (requestId != openRequestId)
        {
            document.Dispose();
            return;
        }

        PublishDocument(document, leftPath, FileTypeDetector.FileKind.Unknown, addToRecents: false);
    }

    /// <summary>
    /// Command-line startup entry point (see <see cref="App"/>): opens <paramref name="first"/>
    /// normally, unless <paramref name="second"/> is also given and both paths detect as
    /// <see cref="FileTypeDetector.FileKind.Json"/> - in which case they're diffed directly via
    /// <see cref="OpenDiffAsync"/> instead of either being opened singly. Detection here is what
    /// lets two JSON paths enter diff mode with no explicit user action; contrast
    /// <see cref="CompareWithAsync"/>, which trusts an already-open document plus an explicit
    /// "Compare with…" pick rather than detecting. When the pair doesn't both classify as JSON
    /// (or <paramref name="second"/> doesn't exist), <paramref name="second"/> is dropped and
    /// <paramref name="first"/> opens alone, with a toast explaining why. Only this command-line
    /// path detects a second file automatically - drag-drop, "Compare with…", and macOS's
    /// Open-With activation are untouched and keep their existing single-file/explicit-diff
    /// behaviour.
    /// </summary>
    public async Task OpenPathsAsync(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(second))
        {
            await OpenPathAsync(first);
            return;
        }

        var normalizedFirst = string.IsNullOrWhiteSpace(first) ? null : Path.GetFullPath(first);
        var normalizedSecond = Path.GetFullPath(second);

        bool bothJson = false;
        if (normalizedFirst is not null && File.Exists(normalizedFirst) && File.Exists(normalizedSecond))
        {
            try
            {
                bothJson = FileTypeDetector.DetectFileType(normalizedFirst) == FileTypeDetector.FileKind.Json
                    && FileTypeDetector.DetectFileType(normalizedSecond) == FileTypeDetector.FileKind.Json;
            }
            catch (Exception ex)
            {
                OpenDebugLog.Write($"OpenPaths: DetectFileType threw: {ex}");
            }
        }

        if (bothJson)
        {
            await OpenDiffAsync(normalizedFirst!, normalizedSecond);
            return;
        }

        await OpenPathAsync(first);
        if (currentFilePath is not null)
            ToastService.Show("Second file ignored — both files must be JSON to open as a diff.");
    }

    public ThemeMode ThemeMode
    {
        get => themeMode;
        private set => SetField(ref themeMode, value);
    }

    /// <summary>Cycles System → Light → Dark → System and persists the choice. The view reacts
    /// to <see cref="ThemeMode"/> to apply the Avalonia theme variant and swap the toggle icon.</summary>
    public void ToggleTheme()
    {
        ThemeMode = ThemeMode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        ThemePreference.Save(ThemeMode);
    }

    public ContentFontMode ContentFontMode
    {
        get => contentFontMode;
        private set => SetField(ref contentFontMode, value);
    }

    /// <summary>Toggles Monospace ↔ SansSerif and persists the choice. The view reacts to
    /// <see cref="ContentFontMode"/> to swap the AppContentFontFamily resource and tooltip.</summary>
    public void ToggleContentFont()
    {
        ContentFontMode = ContentFontMode == ContentFontMode.Monospace
            ? ContentFontMode.SansSerif
            : ContentFontMode.Monospace;
        ContentFontPreference.Save(ContentFontMode);
    }

    public void OpenRecentFile(string path) => _ = OpenPathAsync(path);

    public void ClearRecentFiles()
    {
        RecentFileHistory.Clear();
        ReloadRecentFiles();
    }

    private void ReloadRecentFiles()
    {
        RecentFiles = RecentFileHistory.Load()
            .Select(path => new RecentFileItem(path, Path.GetFileName(path)))
            .ToList();
    }

    /// <summary>
    /// Opens <paramref name="path"/>, replacing any current document. A monotonic
    /// <see cref="openRequestId"/> guards against a newer open superseding this one mid-load;
    /// the loser is disposed here (never published), so its mapping is released.
    /// </summary>
    public async Task OpenPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            OpenDebugLog.Write("OpenPath: path is null/whitespace, returning");
            return;
        }

        var normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath))
        {
            OpenDebugLog.Write($"OpenPath: File.Exists false for '{normalizedPath}'");
            return;
        }

        if (currentFilePath is not null && !string.Equals(currentFilePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            var confirmed = await confirmReplace(
                $"Replace the currently loaded file with \"{Path.GetFileName(normalizedPath)}\"?");
            if (!confirmed)
                return;
        }

        var requestId = ++openRequestId;

        FileTypeDetector.FileKind fileType;
        try
        {
            fileType = FileTypeDetector.DetectFileType(normalizedPath);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenPath: DetectFileType threw: {ex}");
            return;
        }

        OpenDebugLog.Write($"OpenPath: normalizedPath='{normalizedPath}', fileType={fileType}");

        // Stop any search over the outgoing file before its view (and MMapFile) is torn down
        // by the content swap below.
        await DetachFindAsync();
        FindBarResetRequested?.Invoke();
        StatusText = $"Indexing {normalizedPath}… 0%";

        await LoadAndPublishAsync(fileType, normalizedPath, requestId, addToRecents: true);
    }

    /// <summary>
    /// Forces <paramref name="kind"/> onto the currently open file, re-indexing it as that
    /// kind - skipping the "replace file?" confirmation (same file, just a different view) and
    /// the recent-files entry (unlike opening a new path, this isn't a new "recently opened"
    /// event). No-ops if no file is open or <paramref name="kind"/> already matches.
    /// </summary>
    public async Task SwitchViewAsync(FileTypeDetector.FileKind kind)
    {
        if (currentFilePath is null || kind == currentKind)
            return;

        string path = currentFilePath;
        var requestId = ++openRequestId;

        // MUST precede the swap - a live find scan holds spans over the outgoing MMapFile.
        await DetachFindAsync();
        FindBarResetRequested?.Invoke();
        StatusText = $"Indexing {path}… 0%";

        await LoadAndPublishAsync(kind, path, requestId, addToRecents: false);
    }

    /// <summary>
    /// Shared tail of <see cref="OpenPathAsync"/> and <see cref="SwitchViewAsync"/>: pre-flight
    /// compatibility check, load, then publish - or reject into the incompatible-file
    /// placeholder. See the class-level failure-classification diagram in the design doc:
    /// a pre-flight rejection or a zero-progress indexing failure both become an
    /// <see cref="IncompatibleViewModel"/>; a failure with some items indexed publishes the
    /// partial document with the warning banner.
    /// </summary>
    private async Task LoadAndPublishAsync(FileTypeDetector.FileKind kind, string path, int requestId, bool addToRecents)
    {
        string attemptedViewName = DisplayNameFor(kind);

        bool isPlausible;
        string reason;
        try
        {
            isPlausible = FileTypeDetector.IsPlausibleFor(kind, path, out reason);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"LoadAndPublish: IsPlausibleFor threw: {ex}");
            if (requestId == openRequestId)
                StatusText = $"{path} — failed to open";
            return;
        }

        if (!isPlausible)
        {
            if (requestId == openRequestId)
                ShowIncompatible(path, kind, attemptedViewName, new IndexFailure(reason, null, null, null, 0));
            return;
        }

        // Silence the outgoing load's reporter before starting a new one, so a scan being torn
        // down can't write over the incoming file's progress.
        indexProgressReporter?.Stop();
        var reporter = new StatusProgressReporter(this, path, requestId);
        indexProgressReporter = reporter;

        IDocumentViewModel document;
        try
        {
            document = await documentLoader(kind, path, reporter);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"LoadAndPublish: load threw: {ex}");
            if (requestId == openRequestId)
                StatusText = $"{path} — failed to open";
            return;
        }

        // A newer open/switch won the race while we were loading: discard this document (it
        // was never published, so nobody else will dispose it) and leave the newer one in place.
        if (requestId != openRequestId)
        {
            document.Dispose();
            return;
        }

        if (document.IndexFailure is { ItemsIndexed: 0 } failure)
        {
            document.Dispose();
            ShowIncompatible(path, kind, attemptedViewName, failure);
            return;
        }

        PublishDocument(document, path, kind, addToRecents);
        _ = StopProgressWhenIndexedAsync(document, reporter);
    }

    /// <summary>
    /// Hands the status line back to <paramref name="document"/> once its indexing stops, so the
    /// document's final total is the last thing written (see <see cref="StatusProgressReporter.Stop"/>).
    ///
    /// Ordering matters and is load-bearing: the document registered its own continuation on this
    /// same task during load, before this one, so its final <see cref="IDocumentViewModel.StatusText"/>
    /// is written - and mirrored here by <see cref="OnDocumentPropertyChanged"/> - before the
    /// reporter goes quiet. Fire-and-forget from the UI thread; the await resumes there per the
    /// app's threading convention.
    /// </summary>
    private static async Task StopProgressWhenIndexedAsync(IDocumentViewModel document, StatusProgressReporter reporter)
    {
        try
        {
            await document.IndexingTask;
        }
        catch
        {
            // A failed or cancelled scan is the document's to report (IndexFailure/StatusText);
            // either way progress has stopped being meaningful, so the reporter still goes quiet.
        }

        reporter.Stop();
    }

    private static string DisplayNameFor(FileTypeDetector.FileKind kind) =>
        DocumentViewCatalog.Options.FirstOrDefault(o => o.Kind == kind)?.DisplayName ?? kind.ToString();

    /// <summary>
    /// Swaps in the incompatible-file placeholder, keeping <see cref="currentFilePath"/> (and
    /// therefore <see cref="IsFileOpen"/>, the switcher, and the close button) so the user can
    /// switch to a different view or close the file. Does not touch <see cref="findController"/>
    /// - the caller already detached it before attempting the load, and the placeholder has
    /// nothing searchable anyway.
    /// </summary>
    private void ShowIncompatible(string path, FileTypeDetector.FileKind kind, string attemptedViewName, IndexFailure failure)
    {
        // The placeholder's text is final - no scan is still running that could add to it.
        indexProgressReporter?.Stop();

        var incompatible = new IncompatibleViewModel(path, attemptedViewName, failure,
            openAsRawText: () => _ = SwitchViewAsync(FileTypeDetector.FileKind.Unidentified),
            jumpToFailureLocation: () => _ = JumpToRawOffsetAsync(failure.ByteOffset ?? 0));
        SetCurrentDocument(incompatible, path, kind);
    }

    private void PublishDocument(IDocumentViewModel document, string path, FileTypeDetector.FileKind kind, bool addToRecents)
    {
        var navigator = document.CreateSearchNavigator();
        SetCurrentDocument(document, path, kind);
        findController.Attach(navigator);
        IsFindAvailable = navigator is not null;

        if (addToRecents)
        {
            RecentFileHistory.Add(path);
            ReloadRecentFiles();
        }
    }

    /// <summary>
    /// Swaps in a new current document (or clears it when <paramref name="document"/> is null)
    /// and refreshes all the derived shell state that tracks it: status mirroring, title, and
    /// toolbar-bar visibility. The document's own header toolbar (see
    /// <see cref="IDocumentViewModel.Toolbar"/>) follows automatically via its binding to
    /// <see cref="CurrentDocument"/>.
    ///
    /// Disposes the outgoing document here, before the swap: setting <see cref="CurrentDocument"/>
    /// makes Avalonia tear down the old view, and that teardown enumerates the old ListBox's
    /// (whole-file, mmap-backed) ItemsSource once. Disposing first means the collection reports
    /// empty for that walk - instant instead of a multi-second, whole-file materialization, and
    /// reading no unmapped memory - independently of Avalonia's detach/enumerate ordering. Search
    /// is already stopped (callers await FindController.DetachAsync first), and the view's own
    /// DetachedFromVisualTree dispose stays as an idempotent safety net (e.g. window close).
    /// </summary>
    private void SetCurrentDocument(IDocumentViewModel? document, string? path, FileTypeDetector.FileKind kind = FileTypeDetector.FileKind.Unknown)
    {
        if (currentDocument is not null)
        {
            currentDocument.PropertyChanged -= OnDocumentPropertyChanged;
            currentDocument.Dispose();
        }

        currentFilePath = path;
        currentKind = kind;
        CurrentDocument = document;

        // IsFindAvailable is reset here so every path (publish, incompatible, close) starts
        // from a clean slate; PublishDocument raises it back up once it knows the new
        // document's navigator. The failure-derived properties need no reset - they're
        // computed straight from currentDocument, so they just need their change notification
        // raised (below) now that it points at a different document.
        IsFindAvailable = false;

        if (document is not null)
        {
            document.PropertyChanged += OnDocumentPropertyChanged;
            StatusText = document.StatusText;
            FileName = Path.GetFileName(path!);
            Title = document.WindowTitle ?? $"{DefaultTitle} — {FileName}";
            SelectedView = DocumentViewCatalog.Options.FirstOrDefault(o => o.Kind == kind);
        }
        else
        {
            StatusText = "No file loaded";
            FileName = string.Empty;
            Title = DefaultTitle;
            SelectedView = null;
        }

        OnPropertyChanged(nameof(IsFileOpen));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(CanCompare));
        NotifyFailurePropertiesChanged();
    }

    private void NotifyFailurePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsFailureBannerVisible));
        OnPropertyChanged(nameof(FailureLocationText));
        OnPropertyChanged(nameof(CanJumpToFailureLocation));
    }

    /// <summary>
    /// Mirrors the current document's own status line into the shell status bar, and reacts to
    /// its <see cref="IDocumentViewModel.IndexFailure"/> changing after publish: a late failure
    /// with nothing indexed swaps to the incompatible placeholder, one with some items shows
    /// the (permanent, non-dismissible) warning banner.
    /// </summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != currentDocument)
            return;

        if (e.PropertyName is null or nameof(IDocumentViewModel.StatusText))
            StatusText = currentDocument!.StatusText;

        if (e.PropertyName is (null or nameof(IDocumentViewModel.IndexFailure)) && currentDocument!.IndexFailure is { } failure)
        {
            if (failure.ItemsIndexed == 0)
                ShowIncompatible(currentFilePath!, currentKind, DisplayNameFor(currentKind), failure);
            else
                NotifyFailurePropertiesChanged();
        }
    }

    public async Task CloseFileAsync()
    {
        ++openRequestId;
        indexProgressReporter?.Stop();

        // The search scan holds spans over the current view's MMapFile - it must be fully
        // stopped before the content swap detaches (and thereby disposes) that view.
        await DetachFindAsync();
        FindBarResetRequested?.Invoke();

        SetCurrentDocument(null, null);
        ReloadRecentFiles();
    }

    // ── Find ────────────────────────────────────────────────────────────────────────────

    public Task FindAsync(string term, int direction) => findController.FindAsync(term, direction);

    public Task StopFindAsync() => findController.StopAsync();

    private Task DetachFindAsync() => findController.DetachAsync();

    /// <summary>
    /// Writes indexing/search scan progress into <see cref="StatusText"/>. Report is called
    /// from a background scan thread, so it marshals with Dispatcher.UIThread.Post (never a
    /// blocking InvokeAsync) per the app's threading convention. A monotonic request id drops
    /// updates from a superseded open.
    ///
    /// Progress is the shell's only claim on the status line, and it is a temporary one: while
    /// a scan runs, the document's own text is a stale partial count ("250 rows indexed so
    /// far"), so live progress is the more useful thing to show. Once the scan stops, the
    /// document's text becomes the real total and the shell must get out of the way - see
    /// <see cref="Stop"/>.
    /// </summary>
    private sealed class StatusProgressReporter : IProgressReporter
    {
        private const int BucketSize = 5;

        private readonly MainWindowViewModel owner;
        private readonly string path;
        private readonly int requestId;
        private int lastBucket = -1;

        // Set on the UI thread once indexing stops; read on the UI thread inside the posted
        // update. Volatile because Report itself runs on the scan thread.
        private volatile bool stopped;

        public StatusProgressReporter(MainWindowViewModel owner, string path, int requestId)
        {
            this.owner = owner;
            this.path = path;
            this.requestId = requestId;
        }

        /// <summary>
        /// Permanently stops this reporter writing to the status line. Called on the UI thread
        /// when the document's indexing task completes, which is what keeps the final "N tokens"
        /// from being overwritten by a trailing "Indexing… (100%)": the last progress reports are
        /// posted from the scan thread just before the scan completes, so they can still be
        /// sitting in the dispatcher queue at that point. Re-checking the flag inside the posted
        /// action (rather than only before posting) is what drops those already-queued updates -
        /// both sides of that check run on the UI thread, so there is no race left.
        /// </summary>
        public void Stop() => stopped = true;

        public void Report(string message, long? current = null, long? max = null)
        {
            if (stopped || requestId != owner.openRequestId)
                return;

            string text = $"{message} {path}…";

            if (current.HasValue && max.HasValue && max.Value > 0)
            {
                int percent = (int)Math.Min(100, (current.Value * 100L) / max.Value);

                // Only act once per 5% step - a raw byte-offset stream would otherwise post
                // to the UI thread far more often than the status text can usefully change.
                int bucket = percent / BucketSize;
                if (bucket == lastBucket)
                    return;

                lastBucket = bucket;
                text += $" ({percent}%)";
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!stopped && requestId == owner.openRequestId)
                    owner.StatusText = text;
            });
        }
    }
}
