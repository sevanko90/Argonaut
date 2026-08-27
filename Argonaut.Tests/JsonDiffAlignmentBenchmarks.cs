using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
using Argonaut.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Argonaut.Tests;

/// <summary>
/// End-to-end allocation/time coverage for the widest array level JsonDiffIndex will align.
/// Index construction happens in setup, so the measured operation is only diff alignment and
/// record emission. The three shapes exercise the unique-anchor fast path, the capped Myers
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
    private MMapFile leftFile = null!;
    private MMapFile rightFile = null!;
    private JsonStructureIndex leftIndex = null!;
    private JsonStructureIndex rightIndex = null!;

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

        leftFile = new MMapFile(leftPath);
        rightFile = new MMapFile(rightPath);
        var options = new JsonIndexOptions { ComputeContentHashes = true };
        leftIndex = JsonStructureIndex.StartIndexing(leftFile, options);
        rightIndex = JsonStructureIndex.StartIndexing(rightFile, options);
        Task.WaitAll(leftIndex.IndexingTask, rightIndex.IndexingTask);
    }

    [Benchmark]
    public int AlignMaximumArray()
    {
        var diff = JsonDiffIndex.Start(leftIndex, leftFile, rightIndex, rightFile);
        diff.IndexingTask.GetAwaiter().GetResult();
        return diff.RecordCount;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        leftFile.Dispose();
        rightFile.Dispose();
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
