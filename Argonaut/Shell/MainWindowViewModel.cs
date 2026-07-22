using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
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

    private const string DefaultTitle = "Argonaut";

    private readonly Func<string, Task<bool>> confirmReplace;
    private readonly DocumentLoader documentLoader;
    private readonly FindController findController;

    private IDocumentViewModel? currentDocument;
    private string? currentFilePath;
    private string statusText = "No file loaded";
    private string title = DefaultTitle;
    private string fileName = string.Empty;
    private IReadOnlyList<RecentFileItem> recentFiles = Array.Empty<RecentFileItem>();
    private ThemeMode themeMode;
    private ContentFontMode contentFontMode;
    private int openRequestId;

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
        this.documentLoader = documentLoader ?? LoadDocumentAsync;

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

    public IReadOnlyList<RecentFileItem> RecentFiles
    {
        get => recentFiles;
        private set => SetField(ref recentFiles, value);
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

        var reporter = new StatusProgressReporter(this, normalizedPath, requestId);
        IDocumentViewModel document;
        try
        {
            document = await documentLoader(fileType, normalizedPath, reporter);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenPath: load threw: {ex}");
            if (requestId == openRequestId)
                StatusText = $"{normalizedPath} — failed to open";
            return;
        }

        // A newer open won the race while we were loading: discard this document (it was
        // never published, so nobody else will dispose it) and leave the newer one in place.
        if (requestId != openRequestId)
        {
            document.Dispose();
            return;
        }

        PublishDocument(document, normalizedPath);
    }

    private static async Task<IDocumentViewModel> LoadDocumentAsync(
        FileTypeDetector.FileKind fileType, string path, IProgressReporter reporter)
    {
        switch (fileType)
        {
            case FileTypeDetector.FileKind.Json:
            {
                var vm = new JsonViewModel();
                await vm.LoadAsync(path, reporter);
                return vm;
            }
            case FileTypeDetector.FileKind.Ndjson:
            {
                var vm = new NdJsonViewModel();
                await vm.LoadAsync(path, reporter);
                return vm;
            }
            case FileTypeDetector.FileKind.Csv:
            {
                var vm = new CsvViewModel();
                await vm.LoadAsync(path, (byte)',', reporter);
                return vm;
            }
            case FileTypeDetector.FileKind.Tsv:
            {
                var vm = new CsvViewModel();
                await vm.LoadAsync(path, (byte)'\t', reporter);
                return vm;
            }
            case FileTypeDetector.FileKind.Unidentified:
            {
                var vm = new RawViewModel();
                await vm.LoadAsync(path, reporter);
                return vm;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(fileType), fileType, null);
        }
    }

    private void PublishDocument(IDocumentViewModel document, string path)
    {
        SetCurrentDocument(document, path);
        findController.Attach(document.CreateSearchNavigator());
        RecentFileHistory.Add(path);
        ReloadRecentFiles();
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
    private void SetCurrentDocument(IDocumentViewModel? document, string? path)
    {
        if (currentDocument is not null)
        {
            currentDocument.PropertyChanged -= OnDocumentPropertyChanged;
            currentDocument.Dispose();
        }

        currentFilePath = path;
        CurrentDocument = document;

        if (document is not null)
        {
            document.PropertyChanged += OnDocumentPropertyChanged;
            StatusText = document.StatusText;
            FileName = Path.GetFileName(path!);
            Title = $"{DefaultTitle} — {FileName}";
        }
        else
        {
            StatusText = "No file loaded";
            FileName = string.Empty;
            Title = DefaultTitle;
        }

        OnPropertyChanged(nameof(IsFileOpen));
        OnPropertyChanged(nameof(FilePath));
    }

    /// <summary>Mirrors the current document's own status line into the shell status bar.</summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == currentDocument && e.PropertyName is null or nameof(IDocumentViewModel.StatusText))
            StatusText = currentDocument!.StatusText;
    }

    public async Task CloseFileAsync()
    {
        ++openRequestId;

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
    /// </summary>
    private sealed class StatusProgressReporter : IProgressReporter
    {
        private const int BucketSize = 5;

        private readonly MainWindowViewModel owner;
        private readonly string path;
        private readonly int requestId;
        private int lastBucket = -1;

        public StatusProgressReporter(MainWindowViewModel owner, string path, int requestId)
        {
            this.owner = owner;
            this.path = path;
            this.requestId = requestId;
        }

        public void Report(string message, long? current = null, long? max = null)
        {
            if (requestId != owner.openRequestId)
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
                if (requestId == owner.openRequestId)
                    owner.StatusText = text;
            });
        }
    }
}
