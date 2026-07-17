using System;
using System.Threading.Tasks;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.Json;

public sealed class JsonViewModel : IDisposable
{
    private const int InitialTokenTarget = 250;

    private MMapFile? mmap;
    private JsonStructureIndex? index;
    private JsonVisibleRowCollection? rows;

    public string FilePath { get; private set; } = string.Empty;

    public int TokenCount => index?.TokenCount ?? 0;

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    public JsonVisibleRowCollection Rows => rows ?? throw new InvalidOperationException("LoadAsync must complete before Rows is accessed.");

    public JsonViewModel()
    {
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
}
