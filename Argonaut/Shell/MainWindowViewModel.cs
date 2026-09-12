using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
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
        FileTypeDetector.FileKind fileType, IByteOrigin origin, IProgressReporter reporter);

    private const string DefaultTitle = AppInfo.Name;

    /// <summary>What a pasted document is called, since it has no file name.</summary>
    private const string PastedDocumentName = "Pasted text";

    /// <summary>
    /// Largest paste opened rather than declined. Not a clipboard limit - a clipboard will happily
    /// hold far more, and people do copy megabytes out of terminals and query grids. It is a
    /// sanity bound on what is worth holding as one managed array, given that the alternative
    /// (spilling to disk) is not available: see <see cref="PasteAsync"/>.
    /// </summary>
    internal const long MaxPasteBytes = 64L * 1024 * 1024;

    private readonly Func<string, Task<bool>> confirmReplace;
    private readonly Func<Task<byte[]?>>? readClipboardBytes;
    private readonly DocumentLoader documentLoader;
    private readonly FindController findController;

    private IDocumentViewModel? currentDocument;

    // The origins behind the document on screen - one, or two when diffing. Owned here rather
    // than by the document because an origin's lifetime is the open INPUT, not the view: a view
    // swap re-opens a source over the same origin, which is what stops switching from JSON to
    // Raw re-downloading a URL or re-materialising a paste. Released by AdoptOrigins when the
    // input actually changes; see IByteOrigin.
    private readonly List<IByteOrigin> ownedOrigins = new();

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
    private readonly RequestTicket openRequest = new();

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
    public MainWindowViewModel(Func<string, Task<bool>> confirmReplace,
        Func<Task<byte[]?>>? readClipboardBytes = null, DocumentLoader? documentLoader = null)
    {
        this.confirmReplace = confirmReplace;
        this.readClipboardBytes = readClipboardBytes;
        this.documentLoader = documentLoader ?? DocumentViewCatalog.LoadAsync;

        themeMode = ThemePreference.Load();
        contentFontMode = ContentFontPreference.Load();

        findController = new FindController(
            status => FindStatusChanged?.Invoke(status),
            () => currentFilePath is null ? null : new StatusProgressReporter(this, currentFilePath, openRequest.Current));

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

    /// <summary>Whether <see cref="PasteAsync"/> can do anything - false when the view model was
    /// built without a clipboard reader.</summary>
    public bool CanPaste => this.readClipboardBytes is not null;

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
    /// on a truncated value). Asks the current document for the CAPABILITY
    /// (<see cref="IByteOffsetNavigable"/>) rather than matching its concrete type: "jump to an
    /// offset" is meaningful for exactly one view today - every other document kind would have
    /// to implement it as a no-op - so it stays off <see cref="IDocumentViewModel"/>, whose job
    /// is the surface *every* document genuinely shares, but an opt-in interface still lets a
    /// second view honour it one day without a shell edit. The shell holds no concrete-type
    /// match on a document at all (see docs/architecture.md).
    /// </summary>
    public async Task JumpToRawOffsetAsync(long byteOffset)
    {
        if (currentKind != FileTypeDetector.FileKind.Unidentified)
            await SwitchViewAsync(FileTypeDetector.FileKind.Unidentified);

        if (CurrentDocument is IByteOffsetNavigable navigable)
            await navigable.JumpToByteOffsetAsync(byteOffset);
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
        if (this.ownedOrigins.Count == 0 || string.IsNullOrWhiteSpace(rightPath))
            return;

        var normalizedRight = Path.GetFullPath(rightPath);
        if (!File.Exists(normalizedRight))
            return;

        // The left side is the origin already open, not a fresh one over the same path: the
        // diff then shares the current document's materialisation instead of duplicating it.
        await OpenDiffAsync(this.ownedOrigins[0], new FileByteOrigin(normalizedRight));
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
    public async Task OpenDiffAsync(IByteOrigin leftOrigin, IByteOrigin rightOrigin)
    {
        var requestId = openRequest.Begin();
        string leftPath = leftOrigin.Path ?? leftOrigin.DisplayName;
        string rightPath = rightOrigin.Path ?? rightOrigin.DisplayName;

        // UI hygiene: clears the highlight term and the find-bar status before the swap. NOT
        // crash safety - that comes from each search scan owning its own chunk mappings, so a
        // scan still running over the outgoing document holds nothing that document needs back.
        // The outgoing load's reporter must go quiet first either way.
        DetachFind();
        FindBarResetRequested?.Invoke();
        indexProgressReporter?.Stop();
        StatusText = $"Comparing {leftPath} with {rightPath}…";

        var document = new JsonDiffViewModel();
        try
        {
            await document.LoadAsync(leftOrigin, rightOrigin);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenDiff: load threw: {ex}");
            document.Dispose();
            if (rightOrigin != this.ownedOrigins.FirstOrDefault())
                rightOrigin.Dispose();
            if (openRequest.IsCurrent(requestId))
                StatusText = $"{leftPath} — failed to open comparison";
            return;
        }

        if (!openRequest.IsCurrent(requestId))
        {
            document.Dispose();
            rightOrigin.Dispose();
            return;
        }

        PublishDocument(document, leftPath, FileTypeDetector.FileKind.Unknown, addToRecents: false,
            origins: new[] { leftOrigin, rightOrigin });
    }

    /// <summary>
    /// Opens the JSON array occupying <paramref name="request"/>'s byte range as a table,
    /// replacing the current document with a <see cref="JsonArrayTableViewModel"/>. Modelled on
    /// <see cref="OpenDiffAsync"/>, and entered the same way - explicitly, never via
    /// <see cref="FileTypeDetector"/> - so the published document carries
    /// <see cref="FileTypeDetector.FileKind.Unknown"/>: the view switcher shows no selection for
    /// it, and picking any view there re-indexes the origin file as that kind through the normal
    /// switch path, which is a second route back for free. Publishing it as
    /// <see cref="FileTypeDetector.FileKind.Json"/> instead would make
    /// <see cref="SwitchViewAsync"/> no-op on the unchanged kind and strand the user on the
    /// banner link. Not added to recent files - a byte range is not a reopenable path.
    ///
    /// The table reports its own indexing progress (like a diff), so the shell's part is only to
    /// silence the outgoing load's reporter.
    /// </summary>
    public async Task OpenArrayTableAsync(ArrayTableRequest request)
    {
        var requestId = openRequest.Begin();

        // UI hygiene before the swap, exactly as OpenDiffAsync does it - see the remark there
        // for why this is not what makes the swap safe.
        DetachFind();
        FindBarResetRequested?.Invoke();
        indexProgressReporter?.Stop();
        string sourceName = request.Origin.Path ?? request.Origin.DisplayName;
        StatusText = $"Opening {request.ArrayPath} as a table…";

        var document = new JsonArrayTableViewModel();
        try
        {
            await document.LoadAsync(request.Origin, request.Offset, request.Length, request.ArrayPath,
                navigateBack: NavigateBackToJsonAsync);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenArrayTable: load threw: {ex}");
            document.Dispose();
            if (openRequest.IsCurrent(requestId))
                StatusText = $"{sourceName} — failed to open as a table";
            return;
        }

        if (!openRequest.IsCurrent(requestId))
        {
            document.Dispose();
            return;
        }

        // The table is a byte range of the document it came from, so it re-adopts that same
        // origin - nothing new is materialised, and Back re-opens the JSON view over it.
        PublishDocument(document, sourceName, FileTypeDetector.FileKind.Unknown, addToRecents: false,
            origins: new[] { request.Origin });
    }

    /// <summary>
    /// The table's Back: reload the origin file as JSON, then reveal the path the table was
    /// opened from. The reveal is a capability query (<see cref="IPathNavigable"/>), not a type
    /// test - see <see cref="JumpToRawOffsetAsync"/>.
    ///
    /// <see cref="SwitchViewAsync"/> reaches <see cref="SetCurrentDocument"/>, which disposes the
    /// outgoing document - the very table whose toolbar raised this - BEFORE the swap. So
    /// everything after that first await runs with that document already torn down. Nothing here
    /// touches it: <paramref name="originPath"/> is a string the toolbar captured at
    /// construction, and the reveal targets whatever document is current afterwards.
    /// </summary>
    private async Task NavigateBackToJsonAsync(string originPath)
    {
        await SwitchViewAsync(FileTypeDetector.FileKind.Json);

        if (CurrentDocument is IPathNavigable navigable)
            await navigable.NavigateToPathAsync(originPath);
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
        IByteOrigin? firstOrigin = null;
        IByteOrigin? secondOrigin = null;
        if (normalizedFirst is not null && File.Exists(normalizedFirst) && File.Exists(normalizedSecond))
        {
            try
            {
                firstOrigin = new FileByteOrigin(normalizedFirst);
                secondOrigin = new FileByteOrigin(normalizedSecond);
                bothJson = FileTypeDetector.DetectFileType(firstOrigin) == FileTypeDetector.FileKind.Json
                    && FileTypeDetector.DetectFileType(secondOrigin) == FileTypeDetector.FileKind.Json;
            }
            catch (Exception ex)
            {
                OpenDebugLog.Write($"OpenPaths: DetectFileType threw: {ex}");
            }
        }

        if (bothJson)
        {
            await OpenDiffAsync(firstOrigin!, secondOrigin!);
            return;
        }

        firstOrigin?.Dispose();
        secondOrigin?.Dispose();

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
    /// Opens <paramref name="path"/>, replacing any current document. <see cref="openRequest"/>
    /// guards against a newer open superseding this one mid-load; the loser is disposed here
    /// (never published), so its mapping is released.
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

        await OpenOriginAsync(new FileByteOrigin(normalizedPath), addToRecents: true);
    }

    /// <summary>
    /// Reads the clipboard as text and opens it as a document in its own right - no file
    /// involved, so <see cref="IByteOrigin.Path"/> is null and every path-keyed feature skips it.
    /// No-op when no clipboard reader was supplied (the view model can be constructed without
    /// one, and tests usually are).
    ///
    /// Deliberately does NOT spill to a temp file at any size, and cannot: Avalonia's clipboard
    /// API has no streaming read - the whole payload arrives as one array (or one string) before
    /// anything here can look at it, and there is no way to ask the size first. So the process is
    /// holding those bytes either way, and the only thing a spill would change is whether they
    /// stay in managed memory - paid for with a temp file's lifetime, its deletion ordering, and
    /// the Windows "cannot delete a mapped file" hazard. Not worth it; the bytes go straight into
    /// a <see cref="MemoryByteOrigin"/>, and a paste past <see cref="MaxPasteBytes"/> is declined
    /// with a message pointing at the thing the app is actually built for: a file.
    /// </summary>
    public async Task PasteAsync()
    {
        if (this.readClipboardBytes is null)
            return;

        byte[]? bytes;
        try
        {
            bytes = await this.readClipboardBytes();
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"Paste: reading the clipboard threw: {ex}");
            ToastService.Show("Couldn't read the clipboard.");
            return;
        }

        if (bytes is null || bytes.Length == 0)
        {
            ToastService.Show("The clipboard has no text to open.");
            return;
        }

        if (bytes.Length > MaxPasteBytes)
        {
            ToastService.Show($"That's {bytes.Length / (1024 * 1024):N0} MB of text — save it to a file and open that instead.");
            return;
        }

        if (IsFileOpen)
        {
            var confirmed = await confirmReplace("Replace the currently loaded file with the clipboard contents?");
            if (!confirmed)
                return;
        }

        await OpenOriginAsync(new MemoryByteOrigin(bytes, PastedDocumentName), addToRecents: false);
    }

    /// <summary>
    /// Detects and opens <paramref name="origin"/>, replacing any current document. Shared tail of
    /// <see cref="OpenPathAsync"/> and <see cref="PasteAsync"/>: everything from detection onwards
    /// is the same whether the bytes came from a file or not. <see cref="openRequest"/> guards
    /// against a newer open superseding this one mid-load; the loser is disposed here (never
    /// published), so its source is released.
    ///
    /// Takes ownership of <paramref name="origin"/>: it is disposed here if detection fails, and
    /// otherwise passes to the shell's own ownership (see <see cref="AdoptOrigins"/>).
    /// </summary>
    private async Task OpenOriginAsync(IByteOrigin origin, bool addToRecents)
    {
        var requestId = openRequest.Begin();
        string name = origin.Path ?? origin.DisplayName;

        FileTypeDetector.FileKind fileType;
        try
        {
            fileType = FileTypeDetector.DetectFileType(origin);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenOrigin: DetectFileType threw: {ex}");
            origin.Dispose();
            return;
        }

        OpenDebugLog.Write($"OpenOrigin: name='{name}', fileType={fileType}");

        // UI hygiene and defence in depth (see the remark in OpenDiffAsync) - the outgoing
        // document's own source scope is what actually stops a live search before the content
        // swap releases it.
        DetachFind();
        FindBarResetRequested?.Invoke();
        StatusText = $"Indexing {name}… 0%";

        await LoadAndPublishAsync(fileType, origin, requestId, addToRecents);
    }

    /// <summary>
    /// Forces <paramref name="kind"/> onto the currently open file, re-indexing it as that
    /// kind - skipping the "replace file?" confirmation (same file, just a different view) and
    /// the recent-files entry (unlike opening a new path, this isn't a new "recently opened"
    /// event). No-ops if no file is open or <paramref name="kind"/> already matches.
    /// </summary>
    public async Task SwitchViewAsync(FileTypeDetector.FileKind kind)
    {
        if (this.ownedOrigins.Count == 0 || kind == currentKind)
            return;

        // Deliberately the origin already open, not a new one: re-indexing the same input as a
        // different kind must not re-materialise it.
        var origin = this.ownedOrigins[0];
        string path = origin.Path ?? origin.DisplayName;
        var requestId = openRequest.Begin();

        // UI hygiene and defence in depth (see the remark in OpenDiffAsync) - the outgoing
        // document's own mapping scope is what actually stops a live search before the swap.
        DetachFind();
        FindBarResetRequested?.Invoke();
        StatusText = $"Indexing {path}… 0%";

        await LoadAndPublishAsync(kind, origin, requestId, addToRecents: false);
    }

    /// <summary>
    /// Shared tail of <see cref="OpenPathAsync"/> and <see cref="SwitchViewAsync"/>: pre-flight
    /// compatibility check, load, then publish - or reject into the incompatible-file
    /// placeholder. See the class-level failure-classification diagram in the design doc:
    /// a pre-flight rejection or a zero-progress indexing failure both become an
    /// <see cref="IncompatibleViewModel"/>; a failure with some items indexed publishes the
    /// partial document with the warning banner.
    /// </summary>
    private async Task LoadAndPublishAsync(FileTypeDetector.FileKind kind, IByteOrigin origin, long requestId, bool addToRecents)
    {
        string attemptedViewName = DisplayNameFor(kind);
        string path = origin.Path ?? origin.DisplayName;

        bool isPlausible;
        string reason;
        try
        {
            isPlausible = FileTypeDetector.IsPlausibleFor(kind, origin, out reason);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"LoadAndPublish: IsPlausibleFor threw: {ex}");
            if (openRequest.IsCurrent(requestId))
                StatusText = $"{path} — failed to open";
            return;
        }

        if (!isPlausible)
        {
            if (openRequest.IsCurrent(requestId))
                ShowIncompatible(origin, kind, attemptedViewName, new IndexFailure(reason, null, null, null, 0));
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
            document = await documentLoader(kind, origin, reporter);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"LoadAndPublish: load threw: {ex}");
            if (openRequest.IsCurrent(requestId))
                StatusText = $"{path} — failed to open";
            return;
        }

        // A newer open/switch won the race while we were loading: discard this document (it
        // was never published, so nobody else will dispose it) and leave the newer one in place.
        if (!openRequest.IsCurrent(requestId))
        {
            document.Dispose();
            return;
        }

        if (document.IndexFailure is { ItemsIndexed: 0 } failure)
        {
            document.Dispose();
            ShowIncompatible(origin, kind, attemptedViewName, failure);
            return;
        }

        PublishDocument(document, path, kind, addToRecents, origins: new[] { origin });
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
    private void ShowIncompatible(IByteOrigin origin, FileTypeDetector.FileKind kind, string attemptedViewName, IndexFailure failure)
    {
        // The placeholder's text is final - no scan is still running that could add to it.
        indexProgressReporter?.Stop();

        string path = origin.Path ?? origin.DisplayName;
        var incompatible = new IncompatibleViewModel(origin, path, attemptedViewName, failure,
            openAsRawText: () => _ = SwitchViewAsync(FileTypeDetector.FileKind.Unidentified),
            jumpToFailureLocation: () => _ = JumpToRawOffsetAsync(failure.ByteOffset ?? 0));
        SetCurrentDocument(incompatible, path, kind);

        // The placeholder still stands for this input - "open as raw text" re-indexes the same
        // origin - so it is re-adopted rather than released.
        AdoptOrigins(origin);
    }

    private void PublishDocument(IDocumentViewModel document, string path, FileTypeDetector.FileKind kind,
        bool addToRecents, IByteOrigin[] origins)
    {
        var navigator = document.CreateSearchNavigator();
        SetCurrentDocument(document, path, kind);

        // After SetCurrentDocument, which disposed the outgoing document and so released its
        // sources - a temp-file-backed origin cannot be deleted while a mapping over it is open.
        AdoptOrigins(origins);

        findController.Attach(navigator);
        IsFindAvailable = navigator is not null;

        // Only a document that is a file on disk is reopenable, so a paste or a download adds
        // nothing here rather than recording a path that does not exist.
        if (addToRecents && origins.Length > 0 && origins[0].Path is { } diskPath)
        {
            RecentFileHistory.Add(diskPath);
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
    /// is already stopped (callers call FindController.Detach first, for UI state - the swap's
    /// safety no longer depends on it), and the view's own
    /// DetachedFromVisualTree dispose stays as an idempotent safety net (e.g. window close).
    /// </summary>
    /// <summary>
    /// Takes ownership of the origins behind the incoming document and disposes any previously
    /// owned origin that is not among them. Instance identity is the test, deliberately: a view
    /// swap and the array table both re-adopt the very origin already held, and disposing it
    /// would pull a temp-file spill out from under the document being built. Call this only
    /// after the outgoing document has been disposed, so its sources are released first - on
    /// Windows a temp file cannot be deleted while a mapping over it is open.
    /// </summary>
    private void AdoptOrigins(params IByteOrigin[] origins)
    {
        foreach (var owned in this.ownedOrigins)
        {
            if (Array.IndexOf(origins, owned) >= 0)
                continue;

            try { owned.Dispose(); }
            catch (Exception ex) { OpenDebugLog.Write($"AdoptOrigins: dispose threw: {ex}"); }
        }

        this.ownedOrigins.Clear();
        this.ownedOrigins.AddRange(origins);
    }

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
                ShowIncompatible(this.ownedOrigins[0], currentKind, DisplayNameFor(currentKind), failure);
            else
                NotifyFailurePropertiesChanged();
        }
    }

    public async Task CloseFileAsync()
    {
        openRequest.Begin();
        indexProgressReporter?.Stop();

        // UI hygiene and defence in depth (see the remark in OpenDiffAsync) - the outgoing
        // document's own mapping scope is what actually stops a live search before the swap,
        // including via the view's own detach handler, which this await does not drive.
        DetachFind();
        FindBarResetRequested?.Invoke();

        SetCurrentDocument(null, null);
        AdoptOrigins();
        ReloadRecentFiles();
    }

    // ── Find ────────────────────────────────────────────────────────────────────────────

    public Task FindAsync(string term, int direction) => findController.FindAsync(term, direction);

    public void StopFind() => findController.StopSearch();

    private void DetachFind() => findController.Detach();

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
        private readonly long requestId;
        private int lastBucket = -1;

        // Set on the UI thread once indexing stops; read on the UI thread inside the posted
        // update. Volatile because Report itself runs on the scan thread.
        private volatile bool stopped;

        public StatusProgressReporter(MainWindowViewModel owner, string path, long requestId)
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
            if (stopped || !owner.openRequest.IsCurrent(requestId))
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

            ProgressPost.ToUiThread(() =>
            {
                if (!stopped && owner.openRequest.IsCurrent(requestId))
                    owner.StatusText = text;
            });
        }
    }
}
