using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
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
    private IndexFailure? indexFailure;
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

    /// <summary>See <see cref="IDocumentViewModel.IndexFailure"/>.</summary>
    public IndexFailure? IndexFailure
    {
        get => indexFailure;
        private set => SetField(ref indexFailure, value);
    }

    /// <summary>Session state for date hints: the file-level default scheme (inferred or
    /// user-picked) and any per-token overrides. Created eagerly so MainWindow/NdJson can
    /// attach to it before or during load.</summary>
    public DateHintSettings HintSettings { get; } = new();

    /// <summary>Session state for schema hints: the schemas on offer, the selected one and its
    /// parsed document. Created eagerly, like <see cref="HintSettings"/>, so the toolbar and
    /// NdJsonViewModel can attach before or during load.</summary>
    public JsonSchemaSettings SchemaSettings { get; } = new();

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
        SchemaSettings.SchemaChanged += OnSchemaChanged;
        SchemaSettings.PropertyChanged += OnSchemaSettingsPropertyChanged;
    }

    /// <summary>The bound schema changed (a new selection finished loading, or was cleared) -
    /// rebind the rows. Null-safe for the window between selection and the row collection
    /// existing; LoadCore applies whatever is current once it creates the rows.</summary>
    private void OnSchemaChanged(object? sender, EventArgs e)
    {
        if (disposed)
            return;

        rows?.SetSchema(SchemaSettings.Document);
        UpdateSchemaRootMatches();
    }

    /// <summary>
    /// Scores the bound schema's selectable types against the property names this document
    /// actually carries, so the type picker can lead with the likely answers instead of an
    /// alphabetical list of a hundred opaque names.
    ///
    /// Cheap enough to run inline on the UI thread - a bounded key sample the document walk
    /// already has the machinery for, then a linear merge per candidate - and it is only ever
    /// reached for a schema offering a choice at all. Silent when the sample is empty: indexing
    /// may not have reached the root's members yet, and <see cref="MonitorIndexingAsync"/> calls
    /// back once it has.
    /// </summary>
    private void UpdateSchemaRootMatches()
    {
        if (SchemaSettings.Document is not { } schema || schema.NamedRoots.Count == 0 || session is not { } current)
            return;

        var keys = JsonDocumentKeySampler.ReadRootKeys(current.Index, current.File, out bool fromArrayElement);
        if (keys.Count == 0)
            return;

        SchemaSettings.SetRootMatches(JsonSchemaRootMatcher.Rank(schema, keys), fromArrayElement);
    }

    /// <summary>
    /// Persists the schema choice against this document so reopening the file restores it.
    /// Keyed on the *selection* rather than the loaded document, so a schema that fails to parse
    /// is still remembered as the user's choice (they'll want to fix the file, not re-pick it).
    /// Skipped for the nested per-NDJSON-line instances, whose selection is driven from - and
    /// persisted by - the owning NdJsonViewModel.
    ///
    /// The bound root is part of the choice, and lands here a moment after the entry does (the
    /// schema has to parse before its root is known), so this writes twice for one user action -
    /// harmless, and it keeps "what was selected" and "which root of it" in one record.
    /// </summary>
    private void OnSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(JsonSchemaSettings.SelectedEntry) or nameof(JsonSchemaSettings.SelectedRootName)) || Toolbar is null)
            return;

        SchemaSelectionPreference.Save(FilePath, SchemaSettings.SelectedEntry?.FilePath, SchemaSettings.IsRootExplicitlyChosen ? SchemaSettings.SelectedRootName : null);
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
    /// Resolves a JSONPath string (see <see cref="JsonPathResolver"/>) and selects/reveals
    /// the target token if found, or surfaces a toast on parse/lookup failure. Wired into
    /// <see cref="JsonToolbarViewModel"/>'s "Go to path" action.
    ///
    /// Registers the resolve task with the session (RegisterDependentTask) because, on a
    /// still-indexing file, it can await across several ticks while the document is closed -
    /// without this, Dispose could free the mapping while ResolveAsync is still reading it.
    /// </summary>
    public async Task NavigateToPathAsync(string path)
    {
        if (session is null)
        {
            ToastService.Show("No file loaded yet.");
            return;
        }

        var resolveTask = JsonPathResolver.ResolveAsync(session.Index, session.File, path, session.Token);
        session.RegisterDependentTask(resolveTask);

        JsonPathResolveResult result;
        try
        {
            result = await resolveTask;
        }
        catch (Exception ex)
        {
            if (!disposed)
                ToastService.Show($"Navigation failed: {ex.Message}");
            return;
        }

        if (disposed)
            return;

        if (result.TokenIndex is { } tokenIndex)
            SelectToken(tokenIndex);
        else
            ToastService.Show(result.Error ?? "Path not found.");
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
        Toolbar = new JsonToolbarViewModel(HintSettings, SchemaSettings, DefaultExpandDepth, SetDefaultExpandDepth, NavigateToPathAsync,
            refreshSchemaEntries: () => RefreshSchemaEntriesAsync(path));

        var loadTask = LoadCore(new MMapFile(path), progressReporter);

        // Runs alongside indexing rather than blocking the open: whichever finishes first, the
        // other side picks the schema up (LoadCore applies whatever is current when it creates
        // the rows; OnSchemaChanged handles the reverse order).
        _ = ApplyInitialSchemaAsync(path);

        return loadTask;
    }

    /// <summary>
    /// Populates the schema catalog for this document and applies the initial binding, if any: a
    /// <c>&lt;file&gt;.schema.json</c> sidecar wins, otherwise the schema last bound to this path
    /// (see <see cref="SchemaSelectionPreference"/>). Nothing here is ever an error - a missing
    /// sidecar and an unreadable schema folder both just mean "no schema".
    /// </summary>
    private async Task ApplyInitialSchemaAsync(string documentPath)
    {
        var (entries, preselected, rootName) = await Task.Run(() => JsonSchemaCatalog.GatherForDocument(documentPath));
        if (disposed)
            return;

        SchemaSettings.SetEntries(entries);

        if (preselected is { } entry)
            await SchemaSettings.SelectAsync(entry, rootName);
    }

    /// <summary>Re-lists the schema catalog without touching the current selection - so a
    /// schema dropped into the user folder mid-session shows up next time the combo opens,
    /// rather than requiring a restart. See <see cref="JsonToolbarViewModel.IsSchemaFlyoutOpen"/>.</summary>
    private async Task RefreshSchemaEntriesAsync(string documentPath)
    {
        var (entries, _, _) = await Task.Run(() => JsonSchemaCatalog.GatherForDocument(documentPath));
        if (!disposed)
            SchemaSettings.SetEntries(entries);
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

        if (session.Index.Failure is { } failure)
            IndexFailure = failure;

        rows = new JsonVisibleRowCollection(session.Index, session.File,
            new IValueHintProvider[] { new DateHintProvider(HintSettings) }, DefaultExpandDepth);

        // A schema may already have been selected (sidecar/remembered, or pushed down by
        // NdJsonViewModel) while indexing's initial batch was still being awaited.
        if (SchemaSettings.Document is { } schema)
            rows.SetSchema(schema);

        UpdateSchemaRootMatches();

        // Inference dereferences the mapping, so the session must join it before unmapping.
        session.RegisterDependentTask(InferDefaultDateSchemeAsync(session.Index, session.File, session.Token));

        StatusText = $"{FilePath} — {TokenCount:N0} tokens indexed so far";
        _ = MonitorIndexingAsync(session);
    }

    public ISearchNavigator CreateSearchNavigator() => new JsonSearchNavigator(this);

    /// <summary>
    /// Returns true if the VM can process the specified file type
    /// </summary>
    /// <param name="fileType">Type of file to query</param>
    /// <returns>True if the view model can process the specified file type</returns>
    public bool CanHandleFileType(FileTypeDetector.FileKind fileType)
    {
        return fileType == FileTypeDetector.FileKind.Json;
    }

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
            {
                IndexFailure = session.Index.Failure;
                StatusText = session.Index.Failure is { } failure
                    ? $"{FilePath} — indexing stopped at line {failure.Line?.ToString("N0") ?? "?"}, column {failure.Column?.ToString("N0") ?? "?"} — {failure.ItemsIndexed:N0} tokens shown"
                    : $"{FilePath} — indexing failed";
            }
            return;
        }

        if (disposed)
            return;

        StatusText = $"{FilePath} — {session.Index.ItemCount:N0} {session.Index.ItemNoun}";

        // The root's own members can be spread across the whole file (five keys, each a huge
        // array), so the sample taken at open may have seen only the first few. Now that every
        // token is indexed, re-score against the complete key set. Ranking only, so an earlier
        // partial answer was never wrong to show - just less informed.
        UpdateSchemaRootMatches();
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
