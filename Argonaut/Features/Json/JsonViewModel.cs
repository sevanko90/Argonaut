using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using Argonaut.Shell;
using Avalonia.Threading;

namespace Argonaut.Features.Json;

public sealed class JsonViewModel : ObservableObject, IDocumentViewModel
{
    private const int InitialTokenTarget = 250;

    private IndexedFileSession<JsonStructureIndex>? session;
    private JsonVisibleRowCollection? rows;
    private int? selectedTokenIndex;
    private string? selectedPath;
    private string? highlightTerm;
    private string statusText = string.Empty;
    private IReadOnlyList<JsonPathSegment> selectedPathSegments = Array.Empty<JsonPathSegment>();
    private volatile bool disposed;

    public string FilePath { get; private set; } = string.Empty;

    internal MMapFile? Mmap => session?.File;

    internal JsonStructureIndex? Index => session?.Index;

    public int TokenCount => session?.Index.TokenCount ?? 0;

    public Task IndexingTask => session?.IndexingTask ?? Task.CompletedTask;

    /// <summary>Status-bar line for this document (see <see cref="IDocumentViewModel"/>).
    /// Meaningless (and unread) for the nested per-NDJSON-line instances, which load via
    /// the <see cref="MMapFile"/> overload and are never a shell document.</summary>
    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public JsonVisibleRowCollection Rows => rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    /// <summary>Session state for date hints: the file-level default scheme (inferred or
    /// user-picked) and any per-token overrides. Created eagerly so MainWindow/NdJson can
    /// attach to it before or during load.</summary>
    public DateHintSettings HintSettings { get; } = new();

    /// <summary>
    /// How many container levels to auto-expand when the tree is first built. Must be set
    /// before <see cref="LoadAsync(string,IProgressReporter?)"/>/<see cref="LoadAsync(string,long,long,IProgressReporter?)"/>
    /// completes to affect the initial view - see <see cref="JsonToolbarViewModel"/>'s
    /// expand-depth combo.
    /// </summary>
    public int DefaultExpandDepth { get; set; } = 2;

    /// <summary>This document's header toolbar (see <see cref="IDocumentViewModel.Toolbar"/>).
    /// Null until <see cref="LoadAsync(string,IProgressReporter?)"/> creates it; always null for
    /// the nested per-NDJSON-line instances loaded via the offset/length overload, since those
    /// are never a shell document.</summary>
    public JsonToolbarViewModel? Toolbar { get; private set; }

    object? IDocumentViewModel.Toolbar => Toolbar;

    public int? SelectedTokenIndex
    {
        get => selectedTokenIndex;
        private set => SetField(ref selectedTokenIndex, value);
    }

    public string? SelectedPath
    {
        get => selectedPath;
        private set => SetField(ref selectedPath, value);
    }

    public IReadOnlyList<JsonPathSegment> SelectedPathSegments
    {
        get => selectedPathSegments;
        private set => SetField(ref selectedPathSegments, value);
    }

    /// <summary>
    /// The active find term; rows re-find and highlight it in their displayed text (see
    /// SearchHighlight). Null when no find is active.
    /// </summary>
    public string? HighlightTerm
    {
        get => highlightTerm;
        set => SetField(ref highlightTerm, value);
    }

    public JsonViewModel()
    {
    }

    /// <summary>
    /// Selects a token by index, computes its JSONPath, and ensures it's reachable in the
    /// tree (expanding any collapsed ancestor - see JsonVisibleRowCollection.EnsureVisible).
    /// Model fields are set before EnsureVisible so that if it does trigger a row-list
    /// rebuild, that rebuild observes the new SelectedTokenIndex already in place. Only
    /// walks tokenIndex's ancestor chain (see <see cref="JsonPathBuilder"/>) - cheap
    /// regardless of how large the document is, since it never touches unrelated parts of
    /// the index.
    /// </summary>
    public void SelectToken(int tokenIndex)
    {
        SelectedTokenIndex = tokenIndex;
        SelectedPath = JsonPathBuilder.Build(Index!, Mmap!, tokenIndex);
        SelectedPathSegments = JsonPathBuilder.BuildSegments(Index!, Mmap!, tokenIndex);
        rows?.EnsureVisible(tokenIndex);
    }

    /// <summary>
    /// Changes the default-expand depth and applies it immediately if a file is already
    /// loaded, in addition to affecting future loads.
    /// </summary>
    public void SetDefaultExpandDepth(int depth)
    {
        DefaultExpandDepth = depth;
        rows?.SetDefaultExpandDepth(depth);
    }

    public Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        FilePath = path;
        DefaultExpandDepth = ExpandDepthPreference.Load();
        Toolbar = new JsonToolbarViewModel(HintSettings, DefaultExpandDepth, SetDefaultExpandDepth);
        return LoadCore(new MMapFile(path), progressReporter);
    }

    /// <summary>
    /// Loads the sub-document occupying the byte range [offset, offset + length) of the file
    /// at <paramref name="path"/> - e.g. one line of a larger NDJSON file. Creates and owns
    /// its own independent sub-range mapping (disposed with this view model), so a caller
    /// never allocates a mapping this view model is then responsible for freeing.
    /// </summary>
    public Task LoadAsync(string path, long offset, long length, IProgressReporter? progressReporter = null)
    {
        FilePath = path;
        return LoadCore(new MMapFile(path, offset, length), progressReporter);
    }

    private async Task LoadCore(MMapFile mmap, IProgressReporter? progressReporter)
    {
        var session = IndexedFileSession<JsonStructureIndex>.Start(mmap, JsonStructureIndex.StartIndexing, progressReporter);
        this.session = session;

        // Await a small initial batch so the first paint isn't empty; the row collection
        // then tracks index.TokenCount live as indexing continues in the background.
        await session.Index.WaitForTokenCountAsync(InitialTokenTarget);

        rows = new JsonVisibleRowCollection(session.Index, session.File,
            new IValueHintProvider[] { new DateHintProvider(HintSettings) }, DefaultExpandDepth);

        // Inference dereferences the mapping, so the session must join it before unmapping.
        session.RegisterDependentTask(InferDefaultDateSchemeAsync(session.Index, session.File, session.Token));

        StatusText = $"{FilePath} — {TokenCount:N0} tokens indexed so far";
        _ = MonitorIndexingAsync(session);
    }

    public ISearchNavigator CreateSearchNavigator() => new JsonSearchNavigator(this);

    /// <summary>
    /// Refreshes <see cref="StatusText"/> when background indexing finishes or fails.
    /// Fire-and-forget from LoadCore (UI thread); per the app's threading convention the
    /// await resumes on the UI thread. The disposed check covers cancellation-by-dispose:
    /// a superseded or closed document must not repaint its status as a failure.
    /// </summary>
    private async Task MonitorIndexingAsync(IndexedFileSession<JsonStructureIndex> session)
    {
        try
        {
            await session.IndexingTask;
        }
        catch
        {
            if (!disposed)
                StatusText = $"{FilePath} — indexing failed";
            return;
        }

        if (!disposed)
            StatusText = $"{FilePath} — {session.Index.ItemCount:N0} {session.Index.ItemNoun}";
    }

    /// <summary>
    /// Scans at most DateHintInference.MaxTokensToScan already-indexed tokens in the
    /// background for the first classifiable date value, and sets it as the file default if
    /// found. Never a full-file scan. No-ops if the user has already picked a scheme.
    /// </summary>
    private async Task InferDefaultDateSchemeAsync(JsonStructureIndex index, MMapFile mmap, CancellationToken cancellationToken)
    {
        try
        {
            await index.WaitForTokenCountAsync(DateHintInference.MaxTokensToScan);
            if (disposed)
                return;

            var scheme = await Task.Run(() => disposed ? null : DateHintInference.FindFirstScheme(index, mmap, DateHintInference.MaxTokensToScan), cancellationToken);
            if (scheme is { } s)
                Dispatcher.UIThread.Post(() => { if (!disposed) HintSettings.TrySetInferredDefault(s); });
        }
        catch
        {
            // Indexing failures are surfaced elsewhere (MonitorIndexingAsync); inference
            // simply leaves the default scheme at Off.
        }
    }

    public void Dispose()
    {
        // Idempotent: a nested per-line instance is disposed both by its owning
        // NdJsonViewModel and by its JsonView's detach handler, and shell documents may
        // see both owners in teardown edge cases (see IDocumentViewModel).
        if (disposed)
            return;
        disposed = true;

        // Cancel first so the background scans stop promptly; rows must be disposed
        // (stopping its growth timer, which polls the index and reads the mapping) before
        // session.Dispose joins the indexing/inference tasks and releases the mapping.
        session?.Cancel();
        rows?.Dispose();
        session?.Dispose();
    }
}
