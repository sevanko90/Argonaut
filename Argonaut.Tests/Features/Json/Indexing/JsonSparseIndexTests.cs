using System.Text;
using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Json.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Indexing;

/// <summary>
/// The JSON sparse index against a model built by <c>Utf8JsonReader</c> - with comments skipped
/// and trailing commas allowed, as the tree reads. Promotion and checkpoint sizes are small so a
/// document of tens of KB has large containers at several depths.
/// </summary>
public class JsonSparseIndexTests
{
    private const int Promotion = 256;
    private const int Checkpoint = 64;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TheoryData<int, bool> Documents => new()
    {
        { 1, false }, { 2, false }, { 3, false }, { 4, true }, { 5, true }, { 6, true },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void RecordsExactlyTheContainersThatReachThePromotionSize(int seed, bool jsonc)
    {
        byte[] json = RandomJson.LargeContainers(new Random(seed), elements: 120, jsonc);
        var model = Model(json);
        var index = Build(new MemoryByteSource(json));

        var expected = model.Where(c => c.End - c.Start >= Promotion).ToList();
        Assert.True(expected.Count > 10, "the document should have many large containers");
        Assert.Equal(expected.Select(Describe), Recorded(index.Structure).Select(Describe));
        Assert.True(index.Structure.IsComplete);
        Assert.Equal(json.Length, index.Structure.ScannedTo);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void EveryCheckpointFollowsTheCommaBeforeItsChild(int seed, bool jsonc)
    {
        byte[] json = RandomJson.LargeContainers(new Random(seed), elements: 120, jsonc);
        var model = Model(json).Where(c => c.End - c.Start >= Promotion).ToList();
        var index = Build(new MemoryByteSource(json));

        Assert.True(index.Structure.CheckpointCount > 20);
        for (int i = 0; i < index.Structure.CheckpointCount; i++)
        {
            var checkpoint = index.Structure.GetCheckpoint(i);
            var container = model[checkpoint.Container];
            long childStart = container.ChildStarts[(int)checkpoint.Ordinal];

            Assert.Equal((byte)',', json[checkpoint.Offset - 1]);
            Assert.True(checkpoint.Offset <= childStart);
            Assert.True(checkpoint.Ordinal == 0 || container.ChildStarts[(int)checkpoint.Ordinal - 1] < checkpoint.Offset);
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void EverySourceShapeGivesTheSameIndex(int seed, bool jsonc)
    {
        byte[] json = RandomJson.LargeContainers(new Random(seed), elements: 120, jsonc);
        var whole = Snapshot(Build(new MemoryByteSource(json)));

        Assert.Equal(whole, Snapshot(Build(new SplitByteSource(json, 37))));
        Assert.Equal(whole, Snapshot(Build(new SplitByteSource(json, 4096))));

        var growing = new GrowingByteSource(json, initiallyAvailable: 100);
        var index = JsonSparseIndex.StartIndexing(growing, Promotion, Checkpoint);
        for (long revealed = 100; revealed < json.Length; revealed += 777)
        {
            growing.Reveal(revealed);
            Thread.Sleep(1);
        }

        growing.Reveal(json.Length);
        growing.Seal();
        index.IndexingTask.GetAwaiter().GetResult();
        Assert.Equal(whole, Snapshot(index));
    }

    [Fact]
    public void TheUnicodeFixtureIndexesLikeTheReaderSeesIt()
    {
        byte[] json = File.ReadAllBytes(Fixtures.UnicodeNamesAndValuesJson);
        var model = Model(json);
        var index = JsonSparseIndex.StartIndexing(new MemoryByteSource(json), promotionBytes: 32, checkpointBytes: 16);
        index.IndexingTask.GetAwaiter().GetResult();

        Assert.Equal(model.Where(c => c.End - c.Start >= 32).Select(Describe), Recorded(index.Structure).Select(Describe));
    }

    [Theory]
    [InlineData("[1,2,]", 2)]
    [InlineData("[1,2 , /* x */ ]", 2)]
    [InlineData("[ ]", 0)]
    [InlineData("[ /* ] */ ]", 0)]
    [InlineData("[{}]", 1)]
    [InlineData("[[],[],]", 2)]
    public void ChildCountsIgnoreTrailingCommasAndComments(string text, long children)
    {
        var index = JsonSparseIndex.StartIndexing(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);
        index.IndexingTask.GetAwaiter().GetResult();

        Assert.Equal(children, index.Structure.GetContainer(0).ChildCount);
    }

    [Theory]
    [InlineData("[1, [2, 3]")]
    [InlineData("[1]]")]
    public void AnUnbalancedDocumentFailsTheScan(string text)
    {
        var index = JsonSparseIndex.StartIndexing(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);

        Assert.ThrowsAny<Exception>(() => index.IndexingTask.GetAwaiter().GetResult());
        Assert.True(index.AllItemsPublished);
        Assert.NotNull(index.Failure);
    }

    [Theory]
    [InlineData("[1 2]")]
    [InlineData("{\"a\" 1}")]
    [InlineData("[1, oops]")]
    public void ABalancedButInvalidDocumentFailsWithTheReadersMessage(string text)
    {
        var index = JsonSparseIndex.StartIndexing(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);

        Assert.Throws<JsonDocumentInvalidException>(() => index.IndexingTask.GetAwaiter().GetResult());
        Assert.True(index.AllItemsPublished);
        Assert.NotNull(index.Failure);
        Assert.NotNull(index.Failure!.Line);
        Assert.NotNull(index.Failure.Column);
    }

    [Fact]
    public void AValidDocumentHasNoFailure()
    {
        var index = Build(new MemoryByteSource(RandomJson.LargeContainers(new Random(9), elements: 50, jsonc: true)));

        Assert.Null(index.Failure);
        Assert.True(index.AllItemsPublished);
    }

    [Fact]
    public void StoppingTheScanCancelsBothPassesWithoutAFailure()
    {
        byte[] json = RandomJson.LargeContainers(new Random(10), elements: 50);
        var growing = new GrowingByteSource(json, initiallyAvailable: json.Length / 2);
        using var stopping = new CancellationTokenSource();
        var index = JsonSparseIndex.StartIndexing(growing, Promotion, Checkpoint, cancellationToken: stopping.Token);

        stopping.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => index.IndexingTask.GetAwaiter().GetResult());
        Assert.True(index.AllItemsPublished);
        Assert.Null(index.Failure);
    }

    private sealed record ModelContainer(long Start, long End, int Parent, int Depth, long OrdinalInParent, List<long> ChildStarts, byte Kind);

    /// <summary>Every container in start order, with its children's starts - a property name's
    /// opening quote for an object member.</summary>
    private static List<ModelContainer> Model(byte[] json)
    {
        var containers = new List<ModelContainer>();
        var open = new Stack<int>();
        var reader = new Utf8JsonReader(json, ReaderOptions);
        bool afterName = false;

        while (reader.Read())
        {
            var type = reader.TokenType;
            if (type is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                int closing = open.Pop();
                containers[closing] = containers[closing] with { End = reader.TokenStartIndex + 1 };
                afterName = false;
                continue;
            }

            // A member's child start is its name; the value after the name is not another child.
            if (!afterName && open.Count > 0)
                containers[open.Peek()].ChildStarts.Add(reader.TokenStartIndex);
            afterName = type == JsonTokenType.PropertyName;
            if (afterName)
                continue;

            if (type is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                int parent = open.Count > 0 ? open.Peek() : -1;
                long ordinal = parent >= 0 ? containers[parent].ChildStarts.Count - 1 : 0;
                byte kind = type == JsonTokenType.StartObject ? (byte)JsonTokenKind.StartObject : (byte)JsonTokenKind.StartArray;
                containers.Add(new ModelContainer(reader.TokenStartIndex, -1, parent, open.Count, ordinal, [], kind));
                open.Push(containers.Count - 1);
            }
        }

        return containers;
    }

    private static string Describe(ModelContainer c) =>
        $"{c.Start}-{c.End} depth {c.Depth} #{c.OrdinalInParent} children {c.ChildStarts.Count} kind {c.Kind}";

    private static string Describe(TreeContainer c) =>
        $"{c.Start}-{c.End} depth {c.Depth} #{c.OrdinalInParent} children {c.ChildCount} kind {c.FormatKind}";

    private static IEnumerable<TreeContainer> Recorded(SparseContainerIndex structure) =>
        Enumerable.Range(0, structure.ContainerCount).Select(structure.GetContainer);

    private static JsonSparseIndex Build(IByteSource source)
    {
        var index = JsonSparseIndex.StartIndexing(source, Promotion, Checkpoint);
        index.IndexingTask.GetAwaiter().GetResult();
        return index;
    }

    /// <summary>The whole index as lines, so a mismatch reports the first line that differs.</summary>
    private static List<string> Snapshot(JsonSparseIndex index) =>
        Recorded(index.Structure).Select(c => c.ToString())
            .Concat(Enumerable.Range(0, index.Structure.CheckpointCount).Select(i => index.Structure.GetCheckpoint(i).ToString()))
            .ToList();
}
