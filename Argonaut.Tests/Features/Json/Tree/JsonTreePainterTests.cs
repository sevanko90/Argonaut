using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Schema;
using Argonaut.Features.Json.Tree;
using Argonaut.Tests.Support;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Json.Tree;

/// <summary>
/// What the sparse tree's rows say, held row for row to what the dense tree's rows say for the
/// same document - names, values, collapsed summaries, array indices, date hints and schema
/// labels. The dense tree is the oracle until it is retired.
/// </summary>
public sealed class JsonTreePainterTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"painter-{Guid.NewGuid():N}.json");
    private MMapFile? file;

    public void Dispose()
    {
        file?.Dispose();
        File.Delete(path);
    }

    private sealed record Pair(JsonRow Dense, TreeRow Sparse, List<TreeRun> Runs, string? Marker);

    /// <summary>Both trees over the same bytes at the same depth, zipped row by row.</summary>
    private List<Pair> Zip(byte[] json, int defaultDepth, DateHintSettings? hints = null, JsonSchemaDocument? schema = null)
    {
        File.WriteAllBytes(path, json);
        file = new MMapFile(path);

        var providers = hints is null ? null : new IValueHintProvider[] { new DateHintProvider(hints) };
        var dense = JsonStructureIndex.StartIndexing(file);
        dense.IndexingTask.GetAwaiter().GetResult();
        var denseRows = new JsonVisibleRowCollection(dense, file, providers, defaultDepth);
        if (schema is not null)
            denseRows.SetSchema(schema);

        var sparse = JsonSparseIndex.StartIndexing(file, promotionBytes: 256, checkpointBytes: 64);
        sparse.IndexingTask.GetAwaiter().GetResult();
        var reader = new JsonTreeReader(file);
        var text = new JsonTreeText(file, sparse.Structure, reader);
        var painter = new JsonTreePainter(text, providers, offerArrayTable: true);
        var cursor = new TreeCursor(sparse.Structure, reader, new TreeExpandState(defaultDepth));

        var pairs = new List<Pair>();
        int i = 0;
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext(), i++)
        {
            var runs = new List<TreeRun>();
            painter.AppendRuns(cursor.Current, runs);
            pairs.Add(new Pair((JsonRow)denseRows[i]!, cursor.Current, runs, painter.Marker(cursor.Current)));
        }

        Assert.Equal(denseRows.Count, pairs.Count);
        return pairs;
    }

    private static string? NameOf(List<TreeRun> runs)
        => runs.FirstOrDefault(r => r.Style == TreeRunStyle.Name).Text is { } name ? name[..^2] : null;

    private static string ValueOf(List<TreeRun> runs)
        => runs.First(r => r.Style is not (TreeRunStyle.Name or TreeRunStyle.Hint or TreeRunStyle.Link)).Text;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 30)]
    public void NamesValuesSummariesAndIndicesMatchTheDenseTree(int seed, int defaultDepth)
    {
        foreach (var pair in Zip(RandomJson.LargeContainers(new Random(seed), elements: 30), defaultDepth))
        {
            Assert.Equal(pair.Dense.Name, NameOf(pair.Runs));
            Assert.Equal(pair.Dense.Value, ValueOf(pair.Runs));
            Assert.Equal(pair.Dense.ArrayIndex?.ToString(), pair.Marker);
            Assert.Equal(pair.Dense.CanViewAsTable, pair.Runs.Any(r => r.Link is ViewAsTableLink));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void TheUnicodeFixtureSaysWhatTheDenseTreeSays(int defaultDepth)
    {
        foreach (var pair in Zip(File.ReadAllBytes(Fixtures.UnicodeNamesAndValuesJson), defaultDepth))
        {
            Assert.Equal(pair.Dense.Name, NameOf(pair.Runs));
            Assert.Equal(pair.Dense.Value, ValueOf(pair.Runs));
        }
    }

    [Fact]
    public void AValueTooLongToShowLinksToTheRawViewAtItsContent()
    {
        string payload = new('a', 5000);
        var pair = Zip(Encoding.UTF8.GetBytes($"{{\"payload\":\"{payload}\"}}"), defaultDepth: 1)[1];

        Assert.Equal(pair.Dense.Value, ValueOf(pair.Runs));
        var link = Assert.Single(pair.Runs, r => r.Link is ViewInRawLink);
        Assert.Equal(pair.Dense.TruncatedValueOffset, ((ViewInRawLink)link.Link!).Offset);
        Assert.Contains("4.9 KB", link.Text);
    }

    [Fact]
    public void DateHintsMatchAndLinkToTheirValue()
    {
        var hints = new DateHintSettings();
        hints.SetUserDefault(DateDecodingScheme.JsSeconds);
        byte[] json = Encoding.UTF8.GetBytes("{\"at\":1700000000,\"n\":5,\"list\":[1600000000,\"x\"]}");

        var pairs = Zip(json, defaultDepth: 9, hints);
        Assert.Contains(pairs, p => p.Dense.Hint is not null);
        foreach (var pair in pairs)
        {
            var hint = pair.Runs.FirstOrDefault(r => r.Link is DateSchemeLink);
            Assert.Equal(pair.Dense.Hint, hint.Text?.Trim());
            if (hint.Link is DateSchemeLink link)
                Assert.Equal(pair.Sparse.Node.ValueStart, link.ValueOffset);
        }
    }

    [Fact]
    public void SchemaLabelsMatchTheDenseTree()
    {
        var schema = JsonSchemaLoader.TryParse("""
            {
              "title": "Order",
              "type": "object",
              "properties": {
                "id": { "title": "Order id", "type": "integer" },
                "status": { "description": "Where it is\nsecond line", "enum": ["new", "sent"],
                            "x-enum-titles": { "sent": "Dispatched" } },
                "lines": { "title": "Lines", "type": "array",
                           "items": { "title": "Line", "type": "object",
                                      "properties": { "sku": { "title": "Stock code" } } } }
              }
            }
            """)!;
        byte[] json = Encoding.UTF8.GetBytes("{\"id\":1,\"status\":\"sent\",\"lines\":[{\"sku\":\"a\"},{\"sku\":\"b\",\"other\":2}],\"extra\":true}");

        File.WriteAllBytes(path, json);
        using var mapped = new MMapFile(path);
        var sparse = JsonSparseIndex.StartIndexing(mapped, promotionBytes: 16, checkpointBytes: 8);
        sparse.IndexingTask.GetAwaiter().GetResult();
        var reader = new JsonTreeReader(mapped);
        var text = new JsonTreeText(mapped, sparse.Structure, reader);
        var resolver = new JsonSchemaResolver(sparse.Structure, reader, text) { Schema = schema };
        var gutter = new JsonSchemaGutter(resolver, text);

        var dense = JsonStructureIndex.StartIndexing(mapped);
        dense.IndexingTask.GetAwaiter().GetResult();
        var denseRows = new JsonVisibleRowCollection(dense, mapped, defaultExpandDepth: 9);
        denseRows.SetSchema(schema);

        var cursor = new TreeCursor(sparse.Structure, reader, new TreeExpandState(9));
        int i = 0, labelled = 0;
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext(), i++)
        {
            var denseRow = (JsonRow)denseRows[i]!;
            var tip = gutter.ToolTipFor(cursor.Current);
            Assert.Equal(denseRow.SchemaLabel is not null, tip is not null);
            labelled += tip is null ? 0 : 1;
        }

        Assert.True(labelled >= 5, $"only {labelled} rows were labelled");

        Assert.True(gutter.Width > 0);
        resolver.Schema = null;
        Assert.Equal(0, gutter.Width);
    }
}
