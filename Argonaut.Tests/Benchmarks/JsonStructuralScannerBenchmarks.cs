using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Indexing;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// Skipping a whole document - the root array of each <see cref="JsonShape"/> - with
/// <see cref="JsonStructuralScanner"/> against <c>Utf8JsonReader.Skip</c>, the fallback it
/// hands over to. The ratio is what re-parsing on demand costs under each.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 8)]
public class JsonStructuralScannerBenchmarks
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

    [Benchmark(Baseline = true)]
    public long Utf8JsonReader_Skip()
    {
        var reader = new Utf8JsonReader(file.RequireContiguous(0, (int)file.AvailableLength));
        reader.Read();
        reader.Skip();
        return reader.BytesConsumed;
    }

    [Benchmark]
    public long StructuralScanner_Skip()
    {
        JsonStructuralScanner.TrySkipValue(file, 0, out long end);
        return end;
    }
}
