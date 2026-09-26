using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using BenchmarkDotNet.Attributes;

namespace Argonaut.Tests.Benchmarks;

// Acceptance: plain-name lookup <= 1.10x baseline time, fewer allocations; escaped
// lookup compares logical names correctly without per-sibling strings or scratch growth.
[MemoryDiagnoser]
[ShortRunJob]
public class JsonPathNameBenchmarks
{
    [Params(false, true)] public bool Escaped { get; set; }
    private string path = null!;
    private MMapFile mmap = null!;
    private JsonTreeReader reader = null!;
    private JsonTreeText text = null!;
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
        var index = JsonSparseIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        reader = new JsonTreeReader(mmap);
        text = new JsonTreeText(mmap, index.Structure, reader);
        serializedTarget = prefix + "9999";
        decodedTarget = Encoding.UTF8.GetBytes("key9999");
    }

    [Benchmark(Baseline = true)]
    public long SerializedLookup()
    {
        long position = reader.FirstChildPosition(0);
        while (reader.TryReadChild((byte)JsonTokenKind.StartObject, ref position, out var member, out _))
        {
            if (Encoding.UTF8.GetString(text.NameBytes(member)) == serializedTarget) return member.RowStart;
            position = member.ValueEnd;
        }
        return -1;
    }

    [Benchmark]
    public long DecodedLookup()
    {
        long position = reader.FirstChildPosition(0);
        while (reader.TryReadChild((byte)JsonTokenKind.StartObject, ref position, out var member, out _))
        {
            if (JsonUnescape.EqualsDecodedUtf8(text.NameBytes(member), decodedTarget)) return member.RowStart;
            position = member.ValueEnd;
        }
        return -1;
    }

    [GlobalCleanup]
    public void Cleanup() { mmap.Dispose(); File.Delete(path); }
}
