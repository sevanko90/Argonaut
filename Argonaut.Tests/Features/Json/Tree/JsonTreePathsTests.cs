using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Paths;
using Argonaut.Features.Json.Tree;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Tree;

/// <summary>
/// Paths in the JSON tree: every row's path is the one a <see cref="JsonModel"/> spells for the
/// same value, and resolving it lands back on it - through recorded containers and checkpoints,
/// since the sizes are tiny. Then the grammar case by case: quoting, escapes, the optional
/// <c>$</c>, and the errors a path that leads nowhere reports.
/// </summary>
public sealed class JsonTreePathsTests
{
    private const string SampleJson =
        "{\"a\":1,\"nested\":{\"x\":[1,2,{\"deep\":\"value\"}],\"weird key\":true},\"arr\":[10,20,30]}";

    private static JsonTreePathResult Resolve(JsonTreeHarness tree, string path)
        => JsonTreePaths.Resolve(tree.Index.Structure, tree.Reader, tree.Text, path);

    /// <summary>The path of the row the cursor finds at <paramref name="offset"/>, everything
    /// expanded.</summary>
    private static IReadOnlyList<JsonTreePathSegment> SegmentsAt(JsonTreeHarness tree, long offset)
    {
        var cursor = tree.Cursor(new TreeExpandState(int.MaxValue));
        Assert.True(cursor.SeekTo(offset));
        return JsonTreePaths.Segments(cursor, tree.Text);
    }

    /// <summary>The text of the row a resolved path lands on.</summary>
    private static string Landed(JsonTreeHarness tree, string path)
    {
        var result = Resolve(tree, path);
        Assert.Null(result.Error);
        var row = tree.Rows().First(r => r.Row.Start == result.Target);
        return row.Value;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryRowsPathIsTheModelsAndResolvesBackToIt(int seed)
    {
        byte[] json = RandomJson.LargeContainers(new Random(seed), elements: 40);
        var model = new JsonModel(json);
        var tree = new JsonTreeHarness(json, promotionBytes: 256, checkpointBytes: 64);

        // A random object can repeat a name, and a path then names the first.
        var firstWithPath = new Dictionary<string, long>();
        for (int n = 0; n < model.Nodes.Count; n++)
            firstWithPath.TryAdd(model.Path(n, JsonPathSyntax.FormatMember), model.Nodes[n].RowStart);

        var cursor = tree.Cursor(new TreeExpandState(int.MaxValue));
        var rows = model.Rows(new TreeExpandState(int.MaxValue)).Where(r => !r.IsClose).ToList();
        int i = 0;
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
        {
            if (cursor.Current.Shape == TreeRowShape.Close)
                continue;

            string expected = model.Path(rows[i++].Node, JsonPathSyntax.FormatMember);
            var segments = JsonTreePaths.Segments(cursor, tree.Text);
            Assert.Equal(expected, JsonTreePaths.Format(segments));
            Assert.Equal(cursor.Current.Start, segments[^1].Target);

            var resolved = Resolve(tree, expected);
            Assert.Null(resolved.Error);
            Assert.Equal(firstWithPath[expected], resolved.Target);
        }

        Assert.True(i > 200);
    }

    [Theory]
    [InlineData("\"a\":1", "$.a")]
    [InlineData("30", "$.arr[2]")]
    [InlineData("\"value\"", "$.nested.x[2].deep")]
    [InlineData("true", "$.nested['weird key']")]
    public void PathsAreSpelledInTheGrammar(string at, string expected)
    {
        var tree = new JsonTreeHarness(SampleJson);
        long offset = SampleJson.IndexOf(at, StringComparison.Ordinal);

        var segments = SegmentsAt(tree, offset);

        Assert.Equal(expected, JsonTreePaths.Format(segments));
        Assert.Equal("$", segments[0].Label);
    }

    [Fact]
    public void TheRootIsDollarAlone()
        => Assert.Equal("$", JsonTreePaths.Format(SegmentsAt(new JsonTreeHarness(SampleJson), 0)));

    [Fact]
    public void EachSegmentTargetsTheRowOfAnAncestor()
    {
        var tree = new JsonTreeHarness(SampleJson);
        var segments = SegmentsAt(tree, SampleJson.IndexOf("\"value\"", StringComparison.Ordinal));

        Assert.Equal(new[] { "$", ".nested", ".x", "[2]", ".deep" }, segments.Select(s => s.Label));

        // Where each segment leads: the root, "nested", "x", the third element, "deep".
        var starts = tree.Rows().Select(r => r.Row.Start).ToList();
        Assert.All(segments, s => Assert.Contains(s.Target, starts));
        Assert.Equal(new long[]
        {
            0,
            SampleJson.IndexOf("\"nested\"", StringComparison.Ordinal),
            SampleJson.IndexOf("\"x\"", StringComparison.Ordinal),
            SampleJson.IndexOf("{\"deep\"", StringComparison.Ordinal),
            SampleJson.IndexOf("\"deep\"", StringComparison.Ordinal),
        }, segments.Select(s => s.Target));
    }

    [Theory]
    [InlineData("$", "{")]
    [InlineData("$.a", "1")]
    [InlineData(".a", "1")]
    [InlineData("$.arr[2]", "30")]
    [InlineData("$.nested.x[2].deep", "\"value\"")]
    [InlineData("$.nested['weird key']", "true")]
    [InlineData("$.nested[\"weird key\"]", "true")]
    public void PathsResolve(string path, string value)
        => Assert.Equal(value, Landed(new JsonTreeHarness(SampleJson), path));

    [Theory]
    [InlineData("$.doesNotExist", "No .doesNotExist found under $.")]
    [InlineData("$.arr[99]", "No [99] found under $.arr.")]
    [InlineData("$.nested[0]", "not an array")]
    [InlineData("$.arr.foo", "not an object")]
    [InlineData("$.a.foo", "not an object")]
    public void APathThatLeadsNowhereSaysWhy(string path, string reason)
    {
        var result = Resolve(new JsonTreeHarness(SampleJson), path);

        Assert.Null(result.Target);
        Assert.Contains(reason, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$.")]
    [InlineData("$[")]
    [InlineData("$['unterminated")]
    [InlineData("$.foo bar")]
    [InlineData("$[abc]")]
    public void InvalidSyntaxIsAParseError(string path)
    {
        var result = Resolve(new JsonTreeHarness(SampleJson), path);

        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void AnEmptyFileHasNoPaths()
    {
        var result = Resolve(new JsonTreeHarness(""), "$");

        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
    }

    /// <summary>A name written with escapes resolves by its decoded text, and the path the tree
    /// spells for it resolves back.</summary>
    [Theory]
    [InlineData("\\u0061", "a")]
    [InlineData("a\\\"b", "a\"b")]
    [InlineData("a\\\\b", "a\\b")]
    [InlineData("\\uD83D\\uDE00", "😀")]
    [InlineData("a\\nb", "a\nb")]
    [InlineData("\\u00e9", "é")]
    public void EscapedNamesResolveByTheirDecodedText(string serializedName, string decodedName)
    {
        string json = "{\"" + serializedName + "\":1}";
        var tree = new JsonTreeHarness(json);
        string expected = "$['" + decodedName.Replace("\\", "\\\\").Replace("'", "\\'") + "']";

        Assert.Equal("1", Landed(tree, expected));

        string spelled = JsonTreePaths.Format(SegmentsAt(tree, json.IndexOf(":1", StringComparison.Ordinal) + 1));
        Assert.Equal(decodedName == "a" ? "$.a" : expected, spelled);
        Assert.Equal("1", Landed(tree, spelled));
    }

    [Fact]
    public async Task APathResolvesWhileTheIndexIsStillBeingBuilt()
    {
        var json = new StringBuilder("[");
        for (int i = 0; i < 200_000; i++)
            json.Append(i).Append(',');
        json.Append("\"target\"]");
        var bytes = new MemoryByteSource(Encoding.UTF8.GetBytes(json.ToString()));

        var index = JsonSparseIndex.StartIndexing(bytes);
        var reader = new JsonTreeReader(bytes);
        var result = JsonTreePaths.Resolve(index.Structure, reader, new JsonTreeText(bytes, index.Structure, reader), "$[200000]");

        Assert.Null(result.Error);
        Assert.Equal((byte)'"', bytes.ByteAt(result.Target!.Value));
        await index.IndexingTask;
    }
}
