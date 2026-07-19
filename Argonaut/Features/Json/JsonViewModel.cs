using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;
using Avalonia.Threading;

namespace Argonaut.Features.Json;

public sealed class JsonViewModel : ObservableObject, IDisposable
{
    private const int InitialTokenTarget = 250;

    private IndexedFileSession<JsonStructureIndex>? session;
    private JsonVisibleRowCollection? rows;
    private int? selectedTokenIndex;
    private string? selectedPath;
    private string? highlightTerm;
    private IReadOnlyList<JsonPathSegment> selectedPathSegments = Array.Empty<JsonPathSegment>();
    private volatile bool disposed;

    public string FilePath { get; private set; } = string.Empty;

    internal MMapFile? Mmap => session?.File;

    internal JsonStructureIndex? Index => session?.Index;

    public int TokenCount => session?.Index.TokenCount ?? 0;

    public Task IndexingTask => session?.IndexingTask ?? Task.CompletedTask;

    public JsonVisibleRowCollection Rows => rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    /// <summary>Session state for date hints: the file-level default scheme (inferred or
    /// user-picked) and any per-token overrides. Created eagerly so MainWindow/NdJson can
    /// attach to it before or during load.</summary>
    public DateHintSettings HintSettings { get; } = new();

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

    public Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        FilePath = path;
        return LoadCore(new MMapFile(path), progressReporter);
    }

    /// <summary>
    /// Loads from an already-open <see cref="MMapFile"/> instead of a path - e.g. a sub-range
    /// mapping over one line of a larger NDJSON file. This <see cref="JsonViewModel"/> takes
    /// ownership of <paramref name="mmap"/> and disposes it along with itself.
    /// </summary>
    public Task LoadAsync(MMapFile mmap, IProgressReporter? progressReporter = null) => LoadCore(mmap, progressReporter);

    private async Task LoadCore(MMapFile mmap, IProgressReporter? progressReporter)
    {
        var session = IndexedFileSession<JsonStructureIndex>.Start(mmap, JsonStructureIndex.StartIndexing, progressReporter);
        this.session = session;

        // Await a small initial batch so the first paint isn't empty; the row collection
        // then tracks index.TokenCount live as indexing continues in the background.
        await session.Index.WaitForTokenCountAsync(InitialTokenTarget);

        rows = new JsonVisibleRowCollection(session.Index, session.File,
            new IValueHintProvider[] { new DateHintProvider(HintSettings) });

        // Inference dereferences the mapping, so the session must join it before unmapping.
        session.RegisterDependentTask(InferDefaultDateSchemeAsync(session.Index, session.File, session.Token));
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
            // Indexing failures are surfaced elsewhere (MonitorJsonCompletionAsync); inference
            // simply leaves the default scheme at Off.
        }
    }

    public void Dispose()
    {
        disposed = true;

        // Cancel first so the background scans stop promptly; rows must be disposed
        // (stopping its growth timer, which polls the index and reads the mapping) before
        // session.Dispose joins the indexing/inference tasks and releases the mapping.
        session?.Cancel();
        rows?.Dispose();
        session?.Dispose();
    }
}
