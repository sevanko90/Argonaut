using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

public sealed class JsonViewModel : IDisposable, INotifyPropertyChanged
{
    private const int InitialTokenTarget = 250;

    private MMapFile? mmap;
    private JsonStructureIndex? index;
    private JsonVisibleRowCollection? rows;
    private int? selectedTokenIndex;
    private string? selectedPath;
    private string? highlightTerm;
    private IReadOnlyList<JsonPathSegment> selectedPathSegments = Array.Empty<JsonPathSegment>();

    public string FilePath { get; private set; } = string.Empty;

    internal MMapFile? Mmap => mmap;

    internal JsonStructureIndex? Index => index;

    public int TokenCount => index?.TokenCount ?? 0;

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public JsonVisibleRowCollection Rows => rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

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

    public event PropertyChangedEventHandler? PropertyChanged;

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
        SelectedPath = JsonPathBuilder.Build(index!, mmap!, tokenIndex);
        SelectedPathSegments = JsonPathBuilder.BuildSegments(index!, mmap!, tokenIndex);
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
        var index = JsonStructureIndex.StartIndexing(mmap, progressReporter);
        this.mmap = mmap;
        this.index = index;
        IndexingTask = index.IndexingTask;

        // Await a small initial batch so the first paint isn't empty; the row collection
        // then tracks index.TokenCount live as indexing continues in the background.
        await index.WaitForTokenCountAsync(InitialTokenTarget);

        rows = new JsonVisibleRowCollection(index, mmap);
    }

    public void Dispose()
    {
        rows?.Dispose();
        mmap?.Dispose();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
