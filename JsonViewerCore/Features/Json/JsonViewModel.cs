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

    public async Task LoadAsync(string path, IProgressReporter? progressReporter = null)
    {
        FilePath = path;

        var mmap = new MMapFile(path);
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
