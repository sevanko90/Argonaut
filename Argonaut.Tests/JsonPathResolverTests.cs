using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies <see cref="JsonPathResolver"/> resolves the same dot/bracket grammar
/// <see cref="JsonPathBuilder"/> emits, walking top-down (never touching ParentIndex) rather
/// than JsonPathBuilder's bottom-up ParentIndex walk - including round-tripping every path
/// JsonPathBuilder can produce, type-mismatch/not-found errors, parse errors, and resolving
/// while indexing is still running on a large file.
/// </summary>
public class JsonPathResolverTests
{
    private const string SampleJson =
        "{\"a\":1,\"nested\":{\"x\":[1,2,{\"deep\":\"value\"}],\"weird key\":true},\"arr\":[10,20,30]}";

    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    private static int FindTokenIndex(JsonStructureIndex index, System.Func<JsonTokenInfo, bool> predicate)
    {
        for (int i = 0; i < index.TokenCount; i++)
        {
            if (predicate(index.GetToken(i)))
                return i;
        }

        throw new InvalidOperationException("Token not found.");
    }

    [Fact]
    public async Task Root_ResolvesToTokenZero()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$");
            Assert.Equal(0, result.TokenIndex);
            Assert.Null(result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TopLevelMember_Resolves()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            int expected = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.NameLength == 1);
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.a");
            Assert.Equal(expected, result.TokenIndex);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NestedObjectInArray_Resolves()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            int expected = FindTokenIndex(index, t => t.Kind == JsonTokenKind.String && t.NameLength == 4);
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.nested.x[2].deep");
            Assert.Equal(expected, result.TokenIndex);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ArrayElement_Resolves()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.arr[2]");
            Assert.NotNull(result.TokenIndex);
            var token = index.GetToken(result.TokenIndex!.Value);
            Assert.Equal(JsonTokenKind.Number, token.Kind);
            Assert.Equal("30", mmap.GetUtf8String(token.Offset, token.Length));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("$.nested['weird key']")]
    [InlineData("$.nested[\"weird key\"]")]
    public async Task BracketQuotedMemberName_Resolves(string queryPath)
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            int expected = FindTokenIndex(index, t => t.Kind == JsonTokenKind.True);
            var result = await JsonPathResolver.ResolveAsync(index, mmap, queryPath);
            Assert.Equal(expected, result.TokenIndex);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EveryTokenWithAPath_RoundTripsThroughBuilderAndResolver()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            for (int i = 0; i < index.TokenCount; i++)
            {
                var token = index.GetToken(i);
                if (token.Kind is JsonTokenKind.EndObject or JsonTokenKind.EndArray)
                    continue;

                string built = JsonPathBuilder.Build(index, mmap, i);
                var resolved = await JsonPathResolver.ResolveAsync(index, mmap, built);
                Assert.Equal(i, resolved.TokenIndex);
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingMember_ReturnsNotFoundError()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.doesNotExist");
            Assert.Null(result.TokenIndex);
            Assert.NotNull(result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ArrayIndexOutOfRange_ReturnsNotFoundError()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.arr[99]");
            Assert.Null(result.TokenIndex);
            Assert.NotNull(result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IndexingIntoAnObject_ReturnsTypeMismatchError()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.nested[0]");
            Assert.Null(result.TokenIndex);
            Assert.Contains("not an array", result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MemberLookupOnAnArray_ReturnsTypeMismatchError()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.arr.foo");
            Assert.Null(result.TokenIndex);
            Assert.Contains("not an object", result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MemberLookupOnAScalar_ReturnsTypeMismatchError()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$.a.foo");
            Assert.Null(result.TokenIndex);
            Assert.Contains("not an object", result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("$.")]
    [InlineData("$[")]
    [InlineData("$['unterminated")]
    [InlineData("$.foo bar")]
    [InlineData("$[abc]")]
    public async Task InvalidSyntax_ReturnsParseError(string invalidPath)
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            var result = await JsonPathResolver.ResolveAsync(index, mmap, invalidPath);
            Assert.Null(result.TokenIndex);
            Assert.NotNull(result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LeadingDollarIsOptional()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            int expected = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.NameLength == 1);
            var result = await JsonPathResolver.ResolveAsync(index, mmap, ".a");
            Assert.Equal(expected, result.TokenIndex);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolvesWhileIndexingIsStillRunning_OnALargeArray()
    {
        // Large enough that resolution plausibly races a still-running indexer, mirroring
        // JsonOffsetTokenResolverTests' equivalent coverage-wait test.
        var builder = new StringBuilder("[");
        for (int i = 0; i < 200_000; i++)
            builder.Append(i).Append(',');
        builder.Append("\"target\"]");
        string json = builder.ToString();

        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        var mmap = new MMapFile(path);
        try
        {
            var index = JsonStructureIndex.StartIndexing(mmap);

            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$[200000]");

            Assert.NotNull(result.TokenIndex);
            var token = index.GetToken(result.TokenIndex!.Value);
            Assert.Equal(JsonTokenKind.String, token.Kind);
            Assert.Equal("target", mmap.GetUtf8String(token.Offset, token.Length));

            await index.IndexingTask;
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyFile_ReturnsError()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, string.Empty);
        var mmap = new MMapFile(path);
        try
        {
            var index = JsonStructureIndex.StartIndexing(mmap);
            await index.IndexingTask;

            var result = await JsonPathResolver.ResolveAsync(index, mmap, "$");
            Assert.Null(result.TokenIndex);
            Assert.NotNull(result.Error);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
