using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Indexing;

/// <summary>
/// JSON through <see cref="TreeCursor"/>: every row, both directions, and seeks, against the rows a
/// <c>Utf8JsonReader</c> model gives for the same expansion - and against the dense token index's
/// own tree, row for row, which is the test oracle until that index is retired.
/// </summary>
public class JsonTreeReaderTests
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 30)]
    public void RowsMatchTheDenseTokenTree(int seed, int defaultDepth)
    {
        byte[] json = RandomJson.LargeContainers(new Random(seed), elements: 40);
        using var file = new TempJsonFile(json);

        var dense = JsonStructureIndex.StartIndexing(file.Source);
        dense.IndexingTask.GetAwaiter().GetResult();
        var denseRows = new JsonVisibleRowCollection(dense, file.Source, defaultExpandDepth: defaultDepth);
        var expected = new List<(JsonTokenKind, long)>();
        for (int i = 0; i < denseRows.Count; i++)
        {
            var row = (JsonRow)denseRows[i]!;
            expected.Add((row.Kind, row.ValueStart + (row.Kind == JsonTokenKind.String ? 1 : 0)));
        }

        var sparse = JsonSparseIndex.StartIndexing(file.Source, promotionBytes: 256, checkpointBytes: 64);
        sparse.IndexingTask.GetAwaiter().GetResult();
        var cursor = new TreeCursor(sparse.Structure, new JsonTreeReader(file.Source), new TreeExpandState(defaultDepth));
        var actual = new List<(JsonTokenKind, long)>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            actual.Add(AsDenseRow(cursor.Current));

        Assert.True(expected.Count > 40);
        Assert.Equal(expected, actual);
    }

    /// <summary>A cursor row as the dense tree names it: the token kind, and the token's offset -
    /// a string's content, after its opening quote; a closing bracket's own position.</summary>
    private static (JsonTokenKind, long) AsDenseRow(TreeRow row)
    {
        var kind = (JsonTokenKind)row.Node.FormatKind;
        if (row.Shape == TreeRowShape.Close)
            return (kind == JsonTokenKind.StartObject ? JsonTokenKind.EndObject : JsonTokenKind.EndArray, row.Start);

        return (kind, row.Node.ValueStart + (kind == JsonTokenKind.String ? 1 : 0));
    }

    private static void AssertCursorMatchesModel(byte[] json, Random random, int defaultDepth, int seekStride)
    {
        var nodes = Model(json);
        var source = new MemoryByteSource(json);
        var sparse = JsonSparseIndex.StartIndexing(source, promotionBytes: 256, checkpointBytes: 64);
        sparse.IndexingTask.GetAwaiter().GetResult();

        var expand = new TreeExpandState(defaultDepth);
        foreach (var node in nodes.Where(n => n.IsContainer))
        {
            if (random.Next(5) == 0)
                expand.Toggle(node.ValueStart);
        }

        var rows = Flatten(nodes, expand);
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
            int expected = RowShowing(offset, nodes, expand, rows);
            Assert.Equal(rows[expected], Describe(cursor.Current));

            if (expected > 0)
            {
                Assert.True(cursor.MovePrevious());
                Assert.Equal(rows[expected - 1], Describe(cursor.Current));
            }
        }
    }

    private sealed record Node(long RowStart, long ValueStart, long End, int Depth, long Ordinal, bool IsContainer, int Parent, List<int> Children);

    private readonly record struct Row(long ValueStart, bool IsClose, long Start, int Depth, long Ordinal);

    private static Row Describe(TreeRow row) =>
        new(row.Node.ValueStart, row.Shape == TreeRowShape.Close, row.Start, row.Depth, row.Ordinal);

    /// <summary>Every value in document order, a member's row starting at its name.</summary>
    private static List<Node> Model(byte[] json)
    {
        var nodes = new List<Node>();
        var open = new Stack<int>();
        var topLevel = new List<int>();
        var reader = new Utf8JsonReader(json, ReaderOptions);
        long pendingName = -1;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingName = reader.TokenStartIndex;
                    continue;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    int closing = open.Pop();
                    nodes[closing] = nodes[closing] with { End = reader.TokenStartIndex + 1 };
                    continue;
            }

            int parent = open.Count > 0 ? open.Peek() : -1;
            var siblings = parent >= 0 ? nodes[parent].Children : topLevel;
            long start = reader.TokenStartIndex;
            bool isContainer = reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray;
            long end = isContainer ? -1 : reader.BytesConsumed;
            nodes.Add(new Node(pendingName >= 0 ? pendingName : start, start, end, open.Count, siblings.Count, isContainer, parent, []));
            siblings.Add(nodes.Count - 1);
            pendingName = -1;
            if (isContainer)
                open.Push(nodes.Count - 1);
        }

        return nodes;
    }

    private static List<Row> Flatten(List<Node> nodes, TreeExpandState expand)
    {
        var rows = new List<Row>();
        void Append(int i)
        {
            var node = nodes[i];
            rows.Add(new Row(node.ValueStart, false, node.RowStart, node.Depth, node.Ordinal));
            if (!node.IsContainer || !expand.IsExpanded(node.ValueStart, node.Depth))
                return;

            foreach (int child in node.Children)
                Append(child);
            rows.Add(new Row(node.ValueStart, true, node.End - 1, node.Depth, node.Ordinal));
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Parent < 0)
                Append(i);
        }

        return rows;
    }

    private static int RowShowing(long offset, List<Node> nodes, TreeExpandState expand, List<Row> rows)
    {
        var siblings = Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Parent < 0).ToList();
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

    /// <summary>A document written to a temp file and mapped, for the dense index, which the
    /// row collection reads through.</summary>
    private sealed class TempJsonFile : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"tree-{Guid.NewGuid():N}.json");
        private readonly MMapFile file;

        public TempJsonFile(byte[] json)
        {
            File.WriteAllBytes(path, json);
            file = new MMapFile(path);
        }

        public IByteSource Source => file;

        public void Dispose()
        {
            file.Dispose();
            File.Delete(path);
        }
    }
}
