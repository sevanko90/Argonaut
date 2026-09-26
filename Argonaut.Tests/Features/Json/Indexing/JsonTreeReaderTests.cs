using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Indexing;

/// <summary>
/// JSON through <see cref="TreeCursor"/>: every row, both directions, and seeks, against the rows a
/// <see cref="JsonModel"/> gives for the same expansion.
/// </summary>
public class JsonTreeReaderTests
{
    public static TheoryData<int, bool, int> Cases => new()
    {
        { 1, false, 1 }, { 2, false, 3 }, { 3, true, 2 }, { 4, true, 9 }, { 5, false, 0 }, { 6, true, 1 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ForwardBackwardAndSeekMatchTheModel(int seed, bool jsonc, int defaultDepth)
    {
        var random = new Random(seed);
        byte[] json = RandomJson.LargeContainers(random, elements: 60, jsonc);
        AssertCursorMatchesModel(json, random, defaultDepth, seekStride: 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    public void TheUnicodeFixtureWalksAndSeeksLikeTheModel(int defaultDepth)
    {
        byte[] json = File.ReadAllBytes(Fixtures.UnicodeNamesAndValuesJson);
        AssertCursorMatchesModel(json, new Random(defaultDepth), defaultDepth, seekStride: 1);
    }

    private static void AssertCursorMatchesModel(byte[] json, Random random, int defaultDepth, int seekStride)
    {
        var model = new JsonModel(json);
        var source = new MemoryByteSource(json);
        var sparse = JsonSparseIndex.StartIndexing(source, promotionBytes: 256, checkpointBytes: 64);
        sparse.IndexingTask.GetAwaiter().GetResult();

        var expand = new TreeExpandState(defaultDepth);
        foreach (var node in model.Nodes.Where(n => n.IsContainer))
        {
            if (random.Next(5) == 0)
                expand.Toggle(node.ValueStart);
        }

        var rows = model.Rows(expand).Select(r => Describe(model, r)).ToList();
        var cursor = new TreeCursor(sparse.Structure, new JsonTreeReader(source), expand);

        var forward = new List<Row>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            forward.Add(Describe(cursor.Current));
        Assert.Equal(rows, forward);

        var backward = new List<Row>();
        for (bool more = cursor.MoveToEnd(); more; more = cursor.MovePrevious())
            backward.Add(Describe(cursor.Current));
        backward.Reverse();
        Assert.Equal(rows, backward);

        for (long offset = 0; offset < json.Length; offset += seekStride)
        {
            Assert.True(cursor.SeekTo(offset));
            int expected = RowShowing(offset, model, expand, rows);
            Assert.Equal(rows[expected], Describe(cursor.Current));

            if (expected > 0)
            {
                Assert.True(cursor.MovePrevious());
                Assert.Equal(rows[expected - 1], Describe(cursor.Current));
            }
        }
    }

    private readonly record struct Row(long ValueStart, bool IsClose, long Start, int Depth, long Ordinal);

    private static Row Describe(TreeRow row) =>
        new(row.Node.ValueStart, row.Shape == TreeRowShape.Close, row.Start, row.Depth, row.Ordinal);

    private static Row Describe(JsonModel model, JsonModel.Row row)
    {
        var node = model.Nodes[row.Node];
        return new Row(node.ValueStart, row.IsClose, row.IsClose ? node.End - 1 : node.RowStart, node.Depth, node.Ordinal);
    }

    private static int RowShowing(long offset, JsonModel model, TreeExpandState expand, List<Row> rows)
    {
        var nodes = model.Nodes;
        var siblings = model.TopLevel;
        int? enclosing = null;
        while (true)
        {
            int target = siblings.FirstOrDefault(i => offset < nodes[i].End, -1);
            if (target < 0)
            {
                return enclosing is int container
                    ? rows.FindIndex(r => r.ValueStart == nodes[container].ValueStart && r.IsClose)
                    : rows.Count - 1;
            }

            var node = nodes[target];
            if (node.IsContainer && expand.IsExpanded(node.ValueStart, node.Depth) && offset >= node.ValueStart + 1)
            {
                enclosing = target;
                siblings = node.Children;
                continue;
            }

            return rows.FindIndex(r => r.ValueStart == node.ValueStart && !r.IsClose);
        }
    }
}
