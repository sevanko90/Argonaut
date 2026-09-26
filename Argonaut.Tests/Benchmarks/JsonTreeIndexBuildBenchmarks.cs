using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Indexing;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What it costs to index a document for the JSON tree, per <see cref="JsonShape"/>: a full build
/// (whose allocation is the index, reported against the file by
/// <see cref="IndexBytesPerFileByteColumn"/>), and the wait before the first screen of rows can
/// be shown. The baseline for the sparse index in docs/json-sparse-index-plan.md; the sparse
/// index's build joins this class so both are measured over the same files.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 8)]
[Config(typeof(IndexBytesPerFileByteColumn.Config))]
public class JsonTreeIndexBuildBenchmarks
{
    /// <summary>Tokens a first screen can need at the default expand depth of one: the root
    /// plus its first fifty children, at up to ~20 tokens a record.</summary>
    private const int FirstScreenTokens = 1000;

    [Params(JsonShape.TokenDenseArray, JsonShape.DeepNesting, JsonShape.RecordArray)]
    public JsonShape Shape { get; set; }

    [Params(64)]
    public int SizeMiB { get; set; }

    private string path = null!;
    private MMapFile file = null!;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Shape}-{Guid.NewGuid():N}.json");
        JsonShapeCorpus.Write(path, Shape, SizeMiB * 1024L * 1024L);
        file = new MMapFile(path);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        file.Dispose();
        File.Delete(path);
    }

    [Benchmark]
    public int DenseIndex_FullBuild()
    {
        var index = JsonStructureIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();
        return index.TokenCount;
    }

    [Benchmark]
    public int DenseIndex_FirstScreen()
    {
        using var stopping = new CancellationTokenSource();
        var index = JsonStructureIndex.StartIndexing(file, progressReporter: null, stopping.Token);
        while (index.TokenCount < FirstScreenTokens && !index.AllItemsPublished)
            Thread.Yield();

        int reached = index.TokenCount;
        stopping.Cancel();
        try
        {
            index.IndexingTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        return reached;
    }
}
