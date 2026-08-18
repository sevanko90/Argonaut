using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The semantic-equality promises of the opt-in content hashes (see
/// <see cref="JsonContentHasher"/>): key order never matters, array order always does,
/// escaping and number spelling never matter, kinds never collide, and a subtree's hash is
/// invariant under relocation. Plus the off-switch: an index built without the option never
/// allocates the hash log.
/// </summary>
public class JsonContentHashTests
{
    /// <summary>Indexes <paramref name="json"/> with content hashes on and returns the root
    /// token's hash. Each call builds and tears down its own temp file/mapping.</summary>
    private static async Task<long> RootHashAsync(string json)
    {
        var (index, file, path) = await IndexAsync(json);
        try
        {
            return index.GetContentHash(0);
        }
        finally
        {
            file.Dispose();
            File.Delete(path);
        }
    }

    private static async Task<(JsonStructureIndex Index, MMapFile File, string Path)> IndexAsync(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var file = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(file, new JsonIndexOptions { ComputeContentHashes = true });
        await index.IndexingTask;
        return (index, file, path);
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
        var (index, file, path) = await IndexAsync("""{"wrapper":{"x":1,"y":[true,null]},"other":2}""");
        try
        {
            // Token 0 is the root object; token 1 is the "wrapper" container start.
            long nested = index.GetContentHash(1);
            long standalone = await RootHashAsync("""{"x":1,"y":[true,null]}""");
            Assert.Equal(standalone, nested);
        }
        finally
        {
            file.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RelocatedUnderDifferentKeyAndDepth_SameSubtreeHash()
    {
        var (leftIndex, leftFile, leftPath) = await IndexAsync("""{"a":{"x":1,"y":2}}""");
        var (rightIndex, rightFile, rightPath) = await IndexAsync("""{"deep":{"deeper":{"renamed":{"y":2,"x":1}}}}""");
        try
        {
            // Left: token 1 is the {"x":1,"y":2} start. Right: tokens 1,2 are the deep/deeper
            // wrappers, token 3 is the renamed relocated copy (key order also flipped).
            Assert.Equal(leftIndex.GetContentHash(1), rightIndex.GetContentHash(3));
        }
        finally
        {
            leftFile.Dispose();
            rightFile.Dispose();
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task ScalarHash_IndependentOfPropertyName()
    {
        // A member's name is folded into the PARENT's accumulator, never the child's own
        // hash - the invariant every downstream consumer leans on.
        var (index, file, path) = await IndexAsync("""{"a":"same","b":"same"}""");
        try
        {
            Assert.Equal(index.GetContentHash(1), index.GetContentHash(2));
        }
        finally
        {
            file.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OptionsOff_NoHashLog()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "[1,2,3]");
        var file = new MMapFile(path);
        try
        {
            var index = JsonStructureIndex.StartIndexing(file);
            await index.IndexingTask;

            Assert.False(index.HasContentHashes);
            Assert.Throws<InvalidOperationException>(() => index.GetContentHash(0));
        }
        finally
        {
            file.Dispose();
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
