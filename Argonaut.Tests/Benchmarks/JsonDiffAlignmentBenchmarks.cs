using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.Json.Indexing;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// End-to-end allocation/time coverage for the widest array level JsonDiffIndex will align.
/// Index construction happens in setup, so the measured operation is only reading the elements,
/// hashing them from their bytes, alignment and record emission. The three shapes exercise the unique-anchor fast path, the capped Myers
/// fallback, and a large out-of-order anchor set respectively.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class JsonDiffAlignmentBenchmarks
{
    private const int ElementCount = JsonDiffIndex.MaxAlignableArrayElements;

    [Params(AlignmentShape.OneChanged, AlignmentShape.AllChanged, AlignmentShape.Reordered)]
    public AlignmentShape Shape { get; set; }

    private string leftPath = null!;
    private string rightPath = null!;
    private IndexedSourceSession<JsonSparseIndex> left = null!;
    private IndexedSourceSession<JsonSparseIndex> right = null!;

    public enum AlignmentShape
    {
        OneChanged,
        AllChanged,
        Reordered
    }

    [GlobalSetup]
    public void Setup()
    {
        leftPath = Path.Combine(Path.GetTempPath(), $"argonaut-diff-bench-left-{Guid.NewGuid():N}.json");
        rightPath = Path.Combine(Path.GetTempPath(), $"argonaut-diff-bench-right-{Guid.NewGuid():N}.json");

        WriteArray(leftPath, i => i);
        WriteArray(rightPath, Shape switch
        {
            AlignmentShape.OneChanged => i => i == ElementCount - 1 ? -1 : i,
            AlignmentShape.AllChanged => i => i + ElementCount,
            AlignmentShape.Reordered => i => ElementCount - 1 - i,
            _ => throw new ArgumentOutOfRangeException()
        });

        left = IndexedSourceSession<JsonSparseIndex>.Start(new MMapFile(leftPath), JsonSparseIndex.StartIndexingWithContentHashes);
        right = IndexedSourceSession<JsonSparseIndex>.Start(new MMapFile(rightPath), JsonSparseIndex.StartIndexingWithContentHashes);
        Task.WaitAll(left.IndexingTask, right.IndexingTask);
    }

    [Benchmark]
    public int AlignMaximumArray()
    {
        var diff = JsonDiffIndex.Start(left, right);
        diff.IndexingTask.GetAwaiter().GetResult();
        return diff.RecordCount;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        left.Dispose();
        right.Dispose();
        File.Delete(leftPath);
        File.Delete(rightPath);
    }

    private static void WriteArray(string path, Func<int, int> valueAt)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 20);
        writer.Write('[');
        for (int i = 0; i < ElementCount; i++)
        {
            if (i != 0)
                writer.Write(',');
            writer.Write(valueAt(i));
        }
        writer.Write(']');
    }
}
