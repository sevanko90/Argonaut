using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests;

// Acceptance: plain-name lookup <= 1.10x baseline time, fewer allocations; escaped
// lookup compares logical names correctly without per-sibling strings or scratch growth.
[MemoryDiagnoser]
[ShortRunJob]
public class JsonPathNameBenchmarks
{
    [Params(false, true)] public bool Escaped { get; set; }
    private string path = null!;
    private MMapFile mmap = null!;
    private JsonStructureIndex index = null!;
    private string serializedTarget = null!;
    private byte[] decodedTarget = null!;

    [GlobalSetup]
    public void Setup()
    {
        path = Path.GetTempFileName();
        string prefix = Escaped ? "\\u006bey" : "key";
        var json = new StringBuilder("{");
        for (int i = 0; i < 10_000; i++)
        {
            if (i > 0) json.Append(',');
            json.Append('"').Append(prefix).Append(i).Append("\":1");
        }
        File.WriteAllText(path, json.Append('}').ToString());
        mmap = new MMapFile(path);
        index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        serializedTarget = prefix + "9999";
        decodedTarget = Encoding.UTF8.GetBytes("key9999");
    }

    [Benchmark(Baseline = true)]
    public int PreviousSerializedLookup()
    {
        for (int i = 1; i < index.TokenCount - 1; i++)
        {
            var child = index.GetToken(i);
            if (mmap.GetUtf8String(child.NameOffset, child.NameLength) == serializedTarget) return i;
        }
        return -1;
    }

    [Benchmark]
    public int DecodedLookup()
    {
        for (int i = 1; i < index.TokenCount - 1; i++)
        {
            var child = index.GetToken(i);
            if (JsonUnescape.EqualsDecodedUtf8(mmap.GetSpan(child.NameOffset, child.NameLength), decodedTarget)) return i;
        }
        return -1;
    }

    [GlobalCleanup]
    public void Cleanup() { mmap.Dispose(); File.Delete(path); }
}
