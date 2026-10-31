using Argonaut.Engine.Bytes;
using Argonaut.Features.Xml.Indexing;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What it costs to index a document for the XML tree, per <see cref="XmlShape"/>: a full build,
/// whose allocation is the index and the same-name sibling counts, reported against the file by
/// <see cref="IndexBytesPerFileByteColumn"/>. The tree draws its first screen from the bytes before
/// any of it is indexed, so there is no first-screen wait to measure.
///
/// <b>Size.</b> 64 MiB, because this is the one measurement here that is per byte: speed divides by
/// the document's length and the column divides the allocation by it. Read the allocation with that
/// in mind - an index session pays a fixed ~860 KB for its append logs before it records anything
/// (five logs' first 8192-item segment, three of them on the large object heap), so at this size the
/// column reports mostly that fixed cost, not the marginal one. <c>docs/index-benchmarks.md</c>
/// measures the same indexer at 1 GiB, where the fixed share is small.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 8)]
[Config(typeof(IndexBytesPerFileByteColumn.Config))]
public class XmlTreeIndexBuildBenchmarks
{
    [Params(XmlShape.TinyElements, XmlShape.DeepNesting, XmlShape.RecordRoot, XmlShape.TextHeavy)]
    public XmlShape Shape { get; set; }

    [Params(64)]
    public int SizeMiB { get; set; }

    private string path = null!;
    private MMapFile file = null!;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Shape}-{Guid.NewGuid():N}.xml");
        XmlShapeCorpus.Write(path, Shape, SizeMiB * 1024L * 1024L);
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
        var index = XmlSparseIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();
        return index.Structure.ContainerCount + index.Structure.CheckpointCount;
    }
}
