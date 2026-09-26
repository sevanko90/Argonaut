using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;

namespace Argonaut.Tests.Features.Json.Indexing;

/// <summary>
/// The semantic-equality promises of the content hashes (see <see cref="JsonContentHasher"/>):
/// key order never matters, array order always does, escaping and number spelling never matter,
/// kinds never collide, and a subtree's hash is invariant under relocation. Every hash is taken
/// twice - recorded by the validation pass, and read again from the bytes - and must agree. Plus
/// the off-switch: an index built without hashes has none.
/// </summary>
public class JsonContentHashTests
{
    /// <summary>Every value's hash in document order - the root first, then each value before
    /// its children - so index 1 is the root's first child.</summary>
    private static Task<List<long>> HashesAsync(string json)
        => HashesAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));

    private static async Task<List<long>> HashesAsync(byte[] json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, json);
        try
        {
            // Every container recorded, and none: the two ways a hash is found.
            var recorded = await ValueHashesAsync(path, promotionBytes: 1);
            var fromBytes = await ValueHashesAsync(path, JsonSparseIndex.DefaultPromotionBytes);
            Assert.Equal(recorded, fromBytes);
            return recorded;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<List<long>> ValueHashesAsync(string path, int promotionBytes)
    {
        using var file = new MMapFile(path);
        var index = JsonSparseIndex.StartIndexingWithContentHashes(file, promotionBytes, checkpointBytes: 1);
        await index.IndexingTask;
        var reader = new JsonTreeReader(file);
        var text = new JsonTreeText(file, index.Structure, reader);
        var hashes = new List<long>();

        void Walk(byte parentKind, long position)
        {
            while (reader.TryReadChild(parentKind, ref position, out var node, out _))
            {
                long end = text.End(node);
                hashes.Add((long)index.ContentHashes!.Hash(node.ValueStart, end));
                if (node.IsContainer)
                    Walk(node.FormatKind, reader.FirstChildPosition(node.ValueStart));
                position = end;
            }
        }

        Walk(JsonTreeReader.Document, 0);
        return hashes;
    }

    private static async Task<long> RootHashAsync(string json) => (await HashesAsync(json))[0];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RecordedHashes_MatchHashesReadFromTheBytes(int seed)
    {
        // HashesAsync asserts the two agree for every value; a random document with escapes,
        // numbers and deep nesting gives it plenty to disagree on.
        var hashes = await HashesAsync(Support.RandomJson.LargeContainers(new Random(seed), elements: 40));
        Assert.True(hashes.Count > 40);
    }

    [Fact]
    public async Task PropertyReordering_SameRootHash()
    {
        long a = await RootHashAsync("""{"a":1,"b":{"x":[1,2],"y":"s"},"c":null}""");
        long b = await RootHashAsync("""{"c":null,"a":1,"b":{"y":"s","x":[1,2]}}""");
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task ArrayReordering_DifferentRootHash()
    {
        long a = await RootHashAsync("[1,2,3]");
        long b = await RootHashAsync("[3,2,1]");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task EscapedAndLiteralString_SameHash()
    {
        // "café" spelled literally and via a \u escape decode to the same code points.
        long a = await RootHashAsync("{\"k\":\"café\"}");
        long b = await RootHashAsync("{\"k\":\"caf\\u00e9\"}");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1", "1e0")]
    [InlineData("1", "100e-2")]
    [InlineData("100", "1e2")]
    [InlineData("100", "1.0e2")]
    [InlineData("0.5", "5e-1")]
    [InlineData("0.5", "500e-3")]
    [InlineData("0", "-0")]
    [InlineData("0", "0.000")]
    [InlineData("0", "0e5")]
    [InlineData("-1.5", "-15e-1")]
    [InlineData("12300", "1.23e4")]
    public async Task EquivalentNumberSpellings_SameHash(string left, string right)
    {
        long a = await RootHashAsync($"[{left}]");
        long b = await RootHashAsync($"[{right}]");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("1", "2")]
    [InlineData("1", "1.5")]
    [InlineData("0.5", "0.05")]
    [InlineData("1e600", "1e601")]
    public async Task DifferentNumbers_DifferentHash(string left, string right)
    {
        long a = await RootHashAsync($"[{left}]");
        long b = await RootHashAsync($"[{right}]");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task StringOneAndNumberOne_DifferentHash()
    {
        long a = await RootHashAsync("[\"1\"]");
        long b = await RootHashAsync("[1]");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task EmptyObjectAndEmptyArray_DifferentHash()
    {
        long a = await RootHashAsync("{}");
        long b = await RootHashAsync("[]");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task NestedContainerHash_EqualsStandaloneParse()
    {
        // The subtree {"x":1,"y":[true,null]} sits under a key, inside an object, at depth 1
        // here - and is the whole document there. Relocation invariance says the hashes match.
        var hashes = await HashesAsync("""{"wrapper":{"x":1,"y":[true,null]},"other":2}""");

        // Value 0 is the root object; value 1 is "wrapper"'s.
        long standalone = await RootHashAsync("""{"x":1,"y":[true,null]}""");
        Assert.Equal(standalone, hashes[1]);
    }

    [Fact]
    public async Task RelocatedUnderDifferentKeyAndDepth_SameSubtreeHash()
    {
        var left = await HashesAsync("""{"a":{"x":1,"y":2}}""");
        var right = await HashesAsync("""{"deep":{"deeper":{"renamed":{"y":2,"x":1}}}}""");

        // Left: value 1 is {"x":1,"y":2}. Right: values 1,2 are the deep/deeper wrappers,
        // value 3 is the renamed relocated copy (key order also flipped).
        Assert.Equal(left[1], right[3]);
    }

    [Fact]
    public async Task ScalarHash_IndependentOfPropertyName()
    {
        // A member's name is folded into the PARENT's accumulator, never the child's own
        // hash - the invariant every downstream consumer leans on.
        var hashes = await HashesAsync("""{"a":"same","b":"same"}""");
        Assert.Equal(hashes[1], hashes[2]);
    }

    [Fact]
    public async Task NotAskedFor_NoHashes()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "[1,2,3]");
        try
        {
            using var file = new MMapFile(path);
            var index = JsonSparseIndex.StartIndexing(file);
            await index.IndexingTask;

            Assert.Null(index.ContentHashes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Released_Throws()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "[1,2,3]");
        try
        {
            using var file = new MMapFile(path);
            var index = JsonSparseIndex.StartIndexingWithContentHashes(file);
            await index.IndexingTask;

            index.ContentHashes!.Release();
            Assert.Throws<InvalidOperationException>(() => index.ContentHashes.Hash(0, 7));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1.0", "1")]
    [InlineData("1e0", "1")]
    [InlineData("100e-2", "1")]
    [InlineData("1.23e4", "12300")]
    [InlineData("-0", "0")]
    [InlineData("0.000", "0")]
    [InlineData("1.5", "1.5e0")]
    [InlineData("0.5", "5e-1")]
    [InlineData("-2.50", "-2.5e0")]
    [InlineData("1e600", "1e600")]
    [InlineData("1.230e-5", "1.23e-5")]
    public void CanonicalizeNumber_ProducesCanonicalSpelling(string raw, string expected)
    {
        Span<byte> buffer = stackalloc byte[JsonContentHasher.MaxCanonicalNumberLength];
        int length = JsonContentHasher.CanonicalizeNumber(Encoding.UTF8.GetBytes(raw), buffer);
        Assert.True(length >= 0);
        Assert.Equal(expected, Encoding.UTF8.GetString(buffer[..length]));
    }
}
