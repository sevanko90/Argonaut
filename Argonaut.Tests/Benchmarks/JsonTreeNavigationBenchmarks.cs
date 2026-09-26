using Argonaut.Engine.Bytes;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Indexing;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// What it costs to move around a fully indexed document in the JSON tree, per
/// <see cref="JsonShape"/>: a seek to the middle of the file the way a search hit lands (resolve
/// the offset, reveal it, show a screen from there), and paging backward a screen at a time
/// through a fully expanded tree. The baseline for the sparse index's cursor in
/// docs/json-sparse-index-plan.md, which joins this class over the same files.
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
    private JsonStructureIndex index = null!;
    private JsonVisibleRowCollection seekRows = null!;
    private JsonVisibleRowCollection expandedRows = null!;
    private int pagePosition;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), $"bench-{Shape}-{Guid.NewGuid():N}.json");
        JsonShapeCorpus.Write(path, Shape, SizeMiB * 1024L * 1024L);
        file = new MMapFile(path);
        index = JsonStructureIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();

        expandedRows = new JsonVisibleRowCollection(index, file, defaultExpandDepth: FullyExpanded);
        pagePosition = expandedRows.Count / 2;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        expandedRows.Dispose();
        file.Dispose();
        File.Delete(path);
    }

    // A seek expands ancestors permanently, so each one starts from a freshly collapsed tree.
    [IterationSetup(Target = nameof(DenseIndex_SeekToMiddle))]
    public void FreshTree() => seekRows = new JsonVisibleRowCollection(index, file);

    [IterationCleanup(Target = nameof(DenseIndex_SeekToMiddle))]
    public void DropTree() => seekRows.Dispose();

    /// <summary>Returns the row position reached, or -1 where the tree cannot show the target -
    /// past the per-container display cap, which the middle of a large array is.</summary>
    [Benchmark]
    public int DenseIndex_SeekToMiddle()
    {
        long middle = file.AvailableLength / 2;
        if (JsonOffsetTokenResolver.ResolveTokenForOffset(index, middle) is not int token)
            return -1;

        seekRows.EnsureVisible(token);
        if (seekRows.FindVisiblePosition(token) is not int position)
            return -1;

        int end = Math.Min(seekRows.Count, position + ScreenRows);
        for (int row = position; row < end; row++)
            _ = seekRows[row];

        return position;
    }

    /// <summary>One screen further back each call, wrapping at the top, so every call realises
    /// rows the row cache has not seen recently.</summary>
    [Benchmark]
    public int DenseIndex_PageBackward()
    {
        if (pagePosition < ScreenRows)
            pagePosition = expandedRows.Count - 1;

        for (int row = pagePosition; row > pagePosition - ScreenRows; row--)
            _ = expandedRows[row];

        pagePosition -= ScreenRows;
        return pagePosition;
    }
}
