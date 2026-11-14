using System.Text;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests.Features.Json.Schema;

/// <summary>Property names read straight off a window of JSON text - an NDJSON line's start -
/// where the window may end anywhere.</summary>
public sealed class JsonTextKeySamplerTests
{
    private static string[] Read(string json)
        => JsonTextKeySampler.ReadTopLevelKeys(Encoding.UTF8.GetBytes(json)).Select(Encoding.UTF8.GetString).ToArray();

    [Fact]
    public void ReadsTheOutermostObjectsNames_NotNestedOnes()
    {
        Assert.Equal(["a", "b", "c"], Read("""{"a": 1, "b": {"x": [1, {"y": 2}]}, "c": [ {"z": 3} ]}"""));
    }

    [Fact]
    public void AWindowEndingMidValue_KeepsTheNamesBeforeIt()
    {
        Assert.Equal(["a", "b"], Read("""{"a": 1, "b": {"x": [1, 2, 3"""));
        Assert.Equal(["a"], Read("""{"a": "unfinished str"""));
    }

    [Fact]
    public void ANonObject_HasNoNames()
    {
        Assert.Empty(Read("[1, 2]"));
        Assert.Empty(Read("42"));
        Assert.Empty(Read(""));
        Assert.Empty(Read("not json"));
    }

    [Fact]
    public void MalformedTextPartWay_KeepsTheNamesBeforeIt()
    {
        Assert.Equal(["a"], Read("""{"a": 1, oops}"""));
    }

    [Fact]
    public void StopsAtTheCap()
    {
        string json = "{" + string.Join(",", Enumerable.Range(0, JsonDocumentKeySampler.MaxKeys + 10).Select(i => $"\"k{i}\":{i}")) + "}";

        Assert.Equal(JsonDocumentKeySampler.MaxKeys, Read(json).Length);
    }
}
