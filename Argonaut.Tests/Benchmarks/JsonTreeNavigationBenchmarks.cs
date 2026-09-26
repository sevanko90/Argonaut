using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What it costs to move around a fully indexed document in the JSON tree, per
/// <see cref="JsonShape"/>: a seek to the middle of the file the way a search hit lands (seek to the
/// offset, show a screen from there), and paging backward a screen at a time through a fully
/// expanded tree.
/// </summary>
[MemoryDiagnoser]
public class JsonTreeNavigationBenchmarks
{
    private const int ScreenRows = 50;

    /// <summary>Deep enough to expand every container of every shape in the corpus.</summary>
    private const int FullyExpanded = JsonShapeCorpus.NestingDepth + 4;

    [Params(JsonShape.TokenDenseArray, JsonShape.DeepNesting, JsonShape.RecordArray)]
    public JsonShape Shape { get; set; }

    [Params(64)]
    public int SizeMiB { get; set; }

    private string path = null!;
    private MMapFile file = null!;
    private JsonSparseIndex sparse = null!;
    private TreeCursor seekCursor = null!;
    private TreeCursor pageCursor = null!;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Shape}-{Guid.NewGuid():N}.json");
        JsonShapeCorpus.Write(path, Shape, SizeMiB * 1024L * 1024L);
        file = new MMapFile(path);
        sparse = JsonSparseIndex.StartIndexing(file);
        sparse.IndexingTask.GetAwaiter().GetResult();

        // A seek reveals the target, so its ancestors are expanded.
        seekCursor = new TreeCursor(sparse.Structure, new JsonTreeReader(file), new TreeExpandState(FullyExpanded));
        pageCursor = new TreeCursor(sparse.Structure, new JsonTreeReader(file), new TreeExpandState(FullyExpanded));
        pageCursor.SeekTo(file.AvailableLength / 2);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        file.Dispose();
        File.Delete(path);
    }

    /// <summary>Seek to the middle and step through a screen of rows. The rows are positions,
    /// not decoded text - decoding a screen belongs to the painter.</summary>
    [Benchmark]
    public long SparseIndex_SeekToMiddle()
    {
        seekCursor.SeekTo(file.AvailableLength / 2);
        long reached = seekCursor.Current.Start;
        for (int row = 1; row < ScreenRows && seekCursor.MoveNext(); row++)
        {
        }

        return reached;
    }

    /// <summary>One screen further back each call through the fully expanded tree, wrapping at
    /// the top. Nothing is capped: the middle is the middle of the file.</summary>
    [Benchmark]
    public long SparseIndex_PageBackward()
    {
        for (int row = 0; row < ScreenRows; row++)
        {
            if (!pageCursor.MovePrevious())
                pageCursor.MoveToEnd();
        }

        return pageCursor.Current.Start;
    }
}
