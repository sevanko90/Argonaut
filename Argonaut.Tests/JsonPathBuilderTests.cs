using System.Linq;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies <see cref="JsonPathBuilder"/> reconstructs the expected JSONPath purely from
/// the ParentIndex chain and name spans already stored in <see cref="JsonStructureIndex"/>,
/// including array-element index resolution and property-name quoting.
/// </summary>
public class JsonPathBuilderTests
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

        throw new System.InvalidOperationException("Token not found.");
    }

    [Fact]
    public void Root_ReturnsDollarOnly()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            string result = JsonPathBuilder.Build(index, mmap, 0);
            Assert.Equal("$", result);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void TopLevelMember_ReturnsDotSegment()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            int tokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.NameLength == 1);
            string result = JsonPathBuilder.Build(index, mmap, tokenIndex);
            Assert.Equal("$.a", result);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void ArrayElement_ReturnsBracketIndex()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            // "arr": [10, 20, 30] - locate the "30" scalar, which should resolve to arr[2].
            int arrTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.StartArray && t.NameLength == 3);
            var arrToken = index.GetToken(arrTokenIndex);

            int targetIndex = -1;
            for (int i = arrTokenIndex + 1; i < arrToken.EndIndex; i++)
            {
                var t = index.GetToken(i);
                if (t.Kind == JsonTokenKind.Number)
                    targetIndex = i; // keep overwriting to land on the last element (30)
            }

            string result = JsonPathBuilder.Build(index, mmap, targetIndex);
            Assert.Equal("$.arr[2]", result);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void NestedObjectInArray_ReturnsCombinedPath()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            // "nested":{"x":[1,2,{"deep":"value"}], ...} - locate the "value" string.
            int tokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.String && t.NameLength == 4);
            string result = JsonPathBuilder.Build(index, mmap, tokenIndex);
            Assert.Equal("$.nested.x[2].deep", result);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void MemberNameNeedingQuoting_UsesBracketNotation()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            int tokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.True);
            string result = JsonPathBuilder.Build(index, mmap, tokenIndex);
            Assert.Equal("$.nested['weird key']", result);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildSegments_LabelsConcatenateToSameStringAsBuild()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            // "nested":{"x":[1,2,{"deep":"value"}], ...} - locate the "value" string.
            int tokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.String && t.NameLength == 4);

            string expected = JsonPathBuilder.Build(index, mmap, tokenIndex);
            var segments = JsonPathBuilder.BuildSegments(index, mmap, tokenIndex);

            string joined = string.Concat(segments.Select(s => s.Label));
            Assert.Equal(expected, joined);
            Assert.Equal("$.nested.x[2].deep", joined);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildSegments_EachTokenIndexIsAnAncestorOfTheTarget()
    {
        var (index, mmap, path) = BuildIndex(SampleJson);
        try
        {
            int tokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.String && t.NameLength == 4);
            var segments = JsonPathBuilder.BuildSegments(index, mmap, tokenIndex);

            // Root first, target itself last.
            Assert.Equal("$", segments[0].Label);
            Assert.Equal(0, segments[0].TokenIndex);
            Assert.Equal(tokenIndex, segments[^1].TokenIndex);

            // Walking the target's own ParentIndex chain should reproduce every earlier
            // segment's TokenIndex, in reverse order.
            int current = tokenIndex;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                Assert.Equal(segments[i].TokenIndex, current);
                current = index.GetToken(current).ParentIndex;
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
