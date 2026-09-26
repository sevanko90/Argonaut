using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Indexing;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What it costs to index a document for the JSON tree, per <see cref="JsonShape"/>: a full build
/// (whose allocation is the index, reported against the file by
/// <see cref="IndexBytesPerFileByteColumn"/>). The tree draws its first screen from the bytes
/// before any of it is indexed, so there is no first-screen wait to measure.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 8)]
[Config(typeof(IndexBytesPerFileByteColumn.Config))]
public class JsonTreeIndexBuildBenchmarks
{
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
    public int SparseIndex_FullBuild()
    {
        var index = JsonSparseIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();
        return index.Structure.ContainerCount + index.Structure.CheckpointCount;
    }
}
