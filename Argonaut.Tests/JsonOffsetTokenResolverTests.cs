using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies byte-offset → token resolution for every region a search hit can land in:
/// value content, property-name bytes, quotes, structural punctuation, whitespace before a
/// closing bracket, an empty container's body, leading whitespace, and offsets past the last
/// token - plus the coverage wait used while the byte scan is ahead of the indexer.
/// </summary>
public class JsonOffsetTokenResolverTests
{
    private const string Json = "{ \"alpha\": { \"beta\": [1, \"hello\", true ] } }";

    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    private static void WithFixture(Action<JsonStructureIndex> assert)
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            assert(index);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    // Token layout of the fixture:
    // 0 StartObject (root '{')   1 StartObject (alpha's '{')   2 StartArray (beta's '[')
    // 3 Number 1   4 String hello   5 True   6 EndArray   7 EndObject   8 EndObject
    [Fact]
    public void OffsetInsideStringValue_ResolvesToThatToken()
    {
        WithFixture(index =>
        {
            long offset = Json.IndexOf("hello", StringComparison.Ordinal) + 2;
            Assert.Equal(4, JsonOffsetTokenResolver.ResolveTokenForOffset(index, offset));
        });
    }

    [Fact]
    public void OffsetInsidePropertyNameBytes_ResolvesToTheNamedValue()
    {
        WithFixture(index =>
        {
            long betaOffset = Json.IndexOf("beta", StringComparison.Ordinal) + 1;
            Assert.Equal(2, JsonOffsetTokenResolver.ResolveTokenForOffset(index, betaOffset));

            long alphaOffset = Json.IndexOf("alpha", StringComparison.Ordinal);
            Assert.Equal(1, JsonOffsetTokenResolver.ResolveTokenForOffset(index, alphaOffset));
        });
    }

    [Fact]
    public void OffsetOnOpeningBracket_ResolvesToTheContainer()
    {
        WithFixture(index =>
        {
            long offset = Json.IndexOf('[');
            Assert.Equal(2, JsonOffsetTokenResolver.ResolveTokenForOffset(index, offset));
        });
    }

    [Fact]
    public void OffsetOnPunctuationBeforeValue_ResolvesToTheFollowingValue()
    {
        WithFixture(index =>
        {
            long comma = Json.IndexOf("1,", StringComparison.Ordinal) + 1;
            Assert.Equal(4, JsonOffsetTokenResolver.ResolveTokenForOffset(index, comma));
        });
    }

    [Fact]
    public void OffsetInWhitespaceBeforeClosingBracket_ResolvesToTheContainerBeingClosed()
    {
        WithFixture(index =>
        {
            // The space in "true ]" - the enclosing array (token 2).
            long beforeArrayClose = Json.IndexOf("true ]", StringComparison.Ordinal) + 4;
            Assert.Equal(2, JsonOffsetTokenResolver.ResolveTokenForOffset(index, beforeArrayClose));

            // The space in "] }" sits after the array's End token (6), whose ParentIndex
            // mirrors the array's parent: the inner object (token 1).
            long beforeInnerObjectClose = Json.IndexOf("] }", StringComparison.Ordinal) + 1;
            Assert.Equal(1, JsonOffsetTokenResolver.ResolveTokenForOffset(index, beforeInnerObjectClose));

            // The final "} }" gap - the root object (token 0).
            long beforeRootClose = Json.LastIndexOf("} }", StringComparison.Ordinal) + 1;
            Assert.Equal(0, JsonOffsetTokenResolver.ResolveTokenForOffset(index, beforeRootClose));
        });
    }

    [Fact]
    public void OffsetInsideEmptyContainer_ResolvesToTheContainer()
    {
        var (index, mmap, path) = BuildIndex("{ }");
        try
        {
            Assert.Equal(0, JsonOffsetTokenResolver.ResolveTokenForOffset(index, 1));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void OffsetInLeadingWhitespace_ResolvesToTheFirstToken()
    {
        var (index, mmap, path) = BuildIndex("   {\"a\":1}");
        try
        {
            Assert.Equal(0, JsonOffsetTokenResolver.ResolveTokenForOffset(index, 1));
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void OffsetPastLastToken_ClampsToLastToken()
    {
        WithFixture(index =>
        {
            Assert.Equal(index.TokenCount - 1,
                JsonOffsetTokenResolver.ResolveTokenForOffset(index, Json.Length + 100));
        });
    }

    [Fact]
    public void ResolveWhenCovered_CompletedIndex_ResolvesImmediately()
    {
        WithFixture(index =>
        {
            long offset = Json.IndexOf("hello", StringComparison.Ordinal);
            var task = JsonOffsetTokenResolver.ResolveWhenCoveredAsync(index, offset, CancellationToken.None);
            Assert.Equal(4, task.GetAwaiter().GetResult());
        });
    }

    [Fact]
    public async Task ResolveWhenCovered_WaitsForIndexingToReachTheOffset()
    {
        // Large enough that the resolver plausibly races a still-running indexer; the
        // coverage wait must hand back the right token either way.
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
            long offset = json.IndexOf("target", StringComparison.Ordinal);

            int? token = await JsonOffsetTokenResolver.ResolveWhenCoveredAsync(index, offset, CancellationToken.None);

            Assert.NotNull(token);
            var info = index.GetToken(token.Value);
            Assert.Equal(JsonTokenKind.String, info.Kind);
            Assert.Equal(offset, info.Offset);

            await index.IndexingTask;
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
