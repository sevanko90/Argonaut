using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Xml.Indexing;

/// <summary>
/// The XML sparse index against <see cref="XmlModel"/>. Promotion and checkpoint sizes are small
/// so a document of tens of KB has large containers at several depths. What the index records is
/// the plan's tree (<c>docs/xml-view-options.md</c>): only elements whose content holds an
/// element, comment or processing instruction are containers, and a child starts at its first
/// byte - a text run at its first non-whitespace one.
/// </summary>
public class XmlSparseIndexTests
{
    private const int Promotion = 256;
    private const int Checkpoint = 64;

    public static TheoryData<int> Seeds => new() { 1, 2, 3, 4, 5, 6 };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheGeneratedDocumentsAreWellFormedAndTheModelReadsThemAsXmlReaderDoes(int seed)
    {
        byte[] xml = RandomXml.LargeContainers(new Random(seed), elements: 40);

        Assert.Equal(XmlModel.CountWithXmlReader(xml), XmlModel.CountRaw(xml));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RecordsExactlyTheContainersThatReachThePromotionSize(int seed)
    {
        byte[] xml = RandomXml.LargeContainers(new Random(seed), elements: 40);
        var model = XmlModel.Parse(xml);
        var index = Build(new MemoryByteSource(xml));

        var expected = XmlModel.Containers(model).Where(c => c.End - c.Start >= Promotion).ToList();
        Assert.True(expected.Count > 10, "the document should have many large containers");
        Assert.Equal(expected.Select(Describe), Recorded(index.Structure).Select(Describe));
        Assert.True(index.Structure.IsComplete);
        Assert.Equal(xml.Length, index.Structure.ScannedTo);
        Assert.Null(index.Failure);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EveryCheckpointIsTheStartOfItsChild(int seed)
    {
        byte[] xml = RandomXml.LargeContainers(new Random(seed), elements: 40);
        var model = XmlModel.Parse(xml);
        var recorded = XmlModel.Containers(model).Where(c => c.End - c.Start >= Promotion).ToList();
        var index = Build(new MemoryByteSource(xml));

        Assert.True(index.Structure.CheckpointCount > 20);
        for (int i = 0; i < index.Structure.CheckpointCount; i++)
        {
            var checkpoint = index.Structure.GetCheckpoint(i);
            var container = recorded[checkpoint.Container];
            Assert.Equal(model[container.Children[(int)checkpoint.Ordinal]].Start, checkpoint.Offset);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task EverySourceShapeGivesTheSameIndex(int seed)
    {
        byte[] xml = RandomXml.LargeContainers(new Random(seed), elements: 30);
        var whole = Snapshot(Build(new MemoryByteSource(xml)));

        // A piece size of 1 splits every construct at every byte.
        Assert.Equal(whole, Snapshot(Build(new SplitByteSource(xml, 1))));
        Assert.Equal(whole, Snapshot(Build(new SplitByteSource(xml, 7))));
        Assert.Equal(whole, Snapshot(Build(new SplitByteSource(xml, 37))));
        Assert.Equal(whole, Snapshot(Build(new SplitByteSource(xml, 4096))));

        var growing = new GrowingByteSource(xml, initiallyAvailable: 100);
        var index = XmlSparseIndex.StartIndexing(growing, Promotion, Checkpoint);
        for (long revealed = 100; revealed < xml.Length; revealed += 331)
        {
            growing.Reveal(revealed);
            Thread.Sleep(1);
            Assert.False(index.Structure.IsComplete);
        }

        growing.Reveal(xml.Length);
        growing.Seal();
        await index.IndexingTask;
        Assert.Equal(whole, Snapshot(index));
    }

    [Fact]
    public async Task NothingIsFinalisedBeforeTheLengthSettles()
    {
        // The root's close tag has not arrived yet: the root must stay open, not be closed where
        // the data ends as a truncated document's would be.
        byte[] xml = Encoding.UTF8.GetBytes("<root>" + string.Concat(Enumerable.Range(0, 200).Select(i => $"<item n=\"{i}\">{i}</item>")) + "</root>");
        var growing = new GrowingByteSource(xml, initiallyAvailable: xml.Length - 7);
        var index = XmlSparseIndex.StartIndexing(growing, Promotion, Checkpoint);

        while (index.Structure.ScannedTo < xml.Length - 7 - 64)
            await Task.Delay(1);
        Assert.False(index.Structure.IsComplete);
        Assert.True(index.Structure.GetContainer(0).IsOpen);
        Assert.Null(index.Failure);

        growing.Reveal(xml.Length);
        growing.Seal();
        await index.IndexingTask;
        Assert.Equal(xml.Length, index.Structure.GetContainer(0).End);
        Assert.Equal(200, index.Structure.GetContainer(0).ChildCount);
    }

    [Fact]
    public void ATextOnlyElementFarPastThePromotionSizeIsNeverRecorded()
    {
        // The shape of a CityGML gml:posList: one element of text a thousand times the promotion size.
        string coordinates = string.Join(' ', Enumerable.Range(0, 40_000).Select(i => $"{i}.5"));
        byte[] xml = Encoding.UTF8.GetBytes($"<root>\n  <a>x</a>\n  <posList srsDimension=\"3\">{coordinates}</posList>\n  <b/>\n</root>");
        var index = XmlSparseIndex.StartIndexing(new MemoryByteSource(xml), promotionBytes: 256, checkpointBytes: 64);
        index.IndexingTask.GetAwaiter().GetResult();

        Assert.Equal(1, index.Structure.ContainerCount);
        Assert.Equal(0, index.Structure.GetContainer(0).Start);
        Assert.Equal(3, index.Structure.GetContainer(0).ChildCount);
    }

    [Theory]
    [InlineData("<root><![CDATA[" + "x < y " + "]]>tail</root>")]
    [InlineData("<root>  \n  </root>")]
    [InlineData("<root></root>")]
    public void AnElementOfTextCDataOrNothingIsNotAContainer(string text)
    {
        var index = Build(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);

        Assert.Equal(0, index.Structure.ContainerCount);
        Assert.Null(index.Failure);
    }

    [Theory]
    [InlineData("<root><!-- only a comment --></root>", 1)]
    [InlineData("<root><?pi only?></root>", 1)]
    [InlineData("<root>text <b/> more</root>", 3)]
    [InlineData("<root>\n  <a/>\n  <!-- c -->\n  <![CDATA[d]]>\n  <?e?>\n</root>", 4)]
    public void AnElementHoldingAnElementCommentOrInstructionIsAContainer(string text, long children)
    {
        var index = Build(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);

        Assert.Equal(children, index.Structure.GetContainer(0).ChildCount);
        Assert.Null(index.Failure);
    }

    [Fact]
    public void TheDocumentLevelCountsEveryTopLevelNode()
    {
        // Declaration, comment, DOCTYPE, instruction, root, trailing comment: the root is the fifth.
        string text = "<?xml version=\"1.0\"?>\n<!-- c -->\n<!DOCTYPE root [ <!ENTITY e \"]>\"> ]>\n<?pi x?>\n<root><a/></root>\n<!-- end -->\n";
        var index = Build(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);

        Assert.Equal(1, index.Structure.ContainerCount);
        Assert.Equal(4, index.Structure.GetContainer(0).OrdinalInParent);
        Assert.Equal(text.IndexOf("<root>", StringComparison.Ordinal), index.Structure.GetContainer(0).Start);
        Assert.Null(index.Failure);
    }

    [Theory]
    [InlineData("<a>\n  <b>\n  </c>\n</a>", 3, 3, new[] { "</c>", "<b>" })]
    [InlineData("<a>\n  <b>x</b>\n", 3, 1, new[] { "still open" })]
    [InlineData("<a/>\n</b>", 2, 1, new[] { "nothing to close" })]
    [InlineData("<a>\n<!-- never ends", 2, 1, new[] { "comment" })]
    [InlineData("<a>\n<![CDATA[ never ends", 2, 1, new[] { "CDATA" })]
    [InlineData("<a>\n<?pi never ends", 2, 1, new[] { "processing instruction" })]
    [InlineData("<a>\n<b attr=\"never ends>", 2, 1, new[] { "tag" })]
    [InlineData("<a/>\n<b/>", 2, 1, new[] { "root element" })]
    [InlineData("<a/>\ntext", 2, 1, new[] { "outside the root" })]
    [InlineData("<!DOCTYPE a [\n<!ENTITY x 'y'>\n", 1, 1, new[] { "DOCTYPE" })]
    public void AMalformedDocumentFailsWithWhereAndWhy(string text, long line, long column, string[] phrases)
    {
        var index = XmlSparseIndex.StartIndexing(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);

        Assert.ThrowsAny<Exception>(() => index.IndexingTask.GetAwaiter().GetResult());
        Assert.True(index.AllItemsPublished);
        Assert.NotNull(index.Failure);
        Assert.Equal(line, index.Failure!.Line);
        Assert.Equal(column, index.Failure.Column);
        foreach (string phrase in phrases)
            Assert.Contains(phrase, index.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(index.Structure.IsComplete);
    }

    [Fact]
    public void AValidDocumentHasNoFailure()
    {
        var index = Build(new MemoryByteSource(RandomXml.LargeContainers(new Random(9), elements: 20)));

        Assert.Null(index.Failure);
        Assert.True(index.AllItemsPublished);
        Assert.True(index.IndexingTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void CorruptionDoesNotStopTheIndexShortOfTheRestOfTheFile()
    {
        var text = new StringBuilder("<root>");
        for (int i = 0; i < 4000; i++)
            text.Append(i == 100 ? "<item><broken></item>" : $"<item id=\"{i}\"><v>{i}</v></item>");
        byte[] xml = Encoding.UTF8.GetBytes(text.Append("</root>").ToString());
        var index = XmlSparseIndex.StartIndexing(new MemoryByteSource(xml), Promotion, Checkpoint);

        Assert.ThrowsAny<Exception>(() => index.IndexingTask.GetAwaiter().GetResult());
        Assert.Contains("</item>", index.Failure!.Message);
        Assert.True(index.Structure.IsComplete);
        Assert.Equal(xml.Length, index.Structure.ScannedTo);
        Assert.True(index.Structure.GetCheckpoint(index.Structure.CheckpointCount - 1).Offset > xml.Length * 9 / 10);
    }

    [Fact]
    public void ATruncatedDocumentStillIndexesToTheEndOfItsData()
    {
        // The shape of a download cut short: a large root missing its close tag.
        byte[] xml = Encoding.UTF8.GetBytes("<root>\n" + string.Concat(Enumerable.Range(0, 3000).Select(i => $"  <item id=\"{i}\"/>\n")));
        var index = XmlSparseIndex.StartIndexing(new MemoryByteSource(xml), Promotion, Checkpoint);

        Assert.ThrowsAny<Exception>(() => index.IndexingTask.GetAwaiter().GetResult());
        Assert.NotNull(index.Failure);
        Assert.True(index.Structure.IsComplete);
        Assert.Equal(xml.Length, index.Structure.GetContainer(0).End);
        Assert.Equal(3000, index.Structure.GetContainer(0).ChildCount);
    }

    [Fact]
    public void StoppingTheScanEndsItWithoutAFailure()
    {
        byte[] xml = RandomXml.LargeContainers(new Random(10), elements: 20);
        var growing = new GrowingByteSource(xml, initiallyAvailable: xml.Length / 2);
        using var stopping = new CancellationTokenSource();
        var index = XmlSparseIndex.StartIndexing(growing, Promotion, Checkpoint, cancellationToken: stopping.Token);

        stopping.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => index.IndexingTask.GetAwaiter().GetResult());
        Assert.True(index.AllItemsPublished);
        Assert.Null(index.Failure);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SameNameCountsAtEveryCheckpointMatchTheModel(int seed)
    {
        byte[] xml = RandomXml.LargeContainers(new Random(seed), elements: 40);
        var model = XmlModel.Parse(xml);
        var recorded = XmlModel.Containers(model).Where(c => c.End - c.Start >= Promotion).ToList();
        var index = Build(new MemoryByteSource(xml));
        var counts = index.SiblingCounts;

        int compared = 0;
        for (int i = 0; i < index.Structure.CheckpointCount; i++)
        {
            var checkpoint = index.Structure.GetCheckpoint(i);
            var siblings = recorded[checkpoint.Container].Children.Select(c => model[c]).ToList();
            foreach (var key in siblings.Select(s => s.Key).Distinct())
            {
                long expected = siblings.Take((int)checkpoint.Ordinal).Count(s => s.Key == key);
                Assert.True(counts.TryGetBefore(i, KeyOf(key), out long actual), $"checkpoint {i} should hold a snapshot");
                Assert.Equal(expected, actual);
                compared++;
            }
        }

        for (int c = 0; c < recorded.Count; c++)
        {
            var siblings = recorded[c].Children.Select(n => model[n]).ToList();
            foreach (var key in siblings.Select(s => s.Key).Distinct())
            {
                Assert.True(counts.TryGetTotal(c, KeyOf(key), out long total));
                Assert.Equal(siblings.Count(s => s.Key == key), total);
            }

            Assert.True(counts.TryGetTotal(c, KeyOf("never:used"), out long none));
            Assert.Equal(0, none);
        }

        Assert.True(compared > 50);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void AContainerWithTooManyDistinctChildNamesKeepsNoCounts(int distinctNames, bool kept)
    {
        Assert.Equal(16, XmlSiblingCounts.MaxDistinctKeys);

        // Every distinct name appears before the first checkpoint, then the names repeat.
        var text = new StringBuilder("<root>");
        for (int i = 0; i < 2000; i++)
            text.Append($"<n{i % distinctNames}/>");
        var index = Build(new MemoryByteSource(Encoding.UTF8.GetBytes(text.Append("</root>").ToString())));

        Assert.True(index.Structure.CheckpointCount > 10);
        for (int i = 0; i < index.Structure.CheckpointCount; i++)
            Assert.Equal(kept, index.SiblingCounts.TryGetBefore(i, KeyOf("n0"), out _));
        Assert.Equal(kept, index.SiblingCounts.TryGetTotal(0, KeyOf("n0"), out _));
    }

    [Fact]
    public void ARecordedContainerStillOpenHasNoTotalYet()
    {
        byte[] xml = Encoding.UTF8.GetBytes("<root>" + string.Concat(Enumerable.Range(0, 500).Select(i => $"<item>{i}</item>")) + "</root>");
        var growing = new GrowingByteSource(xml, initiallyAvailable: xml.Length / 2);
        var index = XmlSparseIndex.StartIndexing(growing, Promotion, Checkpoint);
        SpinWait.SpinUntil(() => index.Structure.ContainerCount > 0 && index.Structure.CheckpointCount > 5, TimeSpan.FromSeconds(5));

        Assert.False(index.SiblingCounts.TryGetTotal(0, KeyOf("item"), out _));
        Assert.True(index.SiblingCounts.TryGetBefore(0, KeyOf("item"), out _));

        growing.Reveal(xml.Length);
        growing.Seal();
        index.IndexingTask.GetAwaiter().GetResult();
        Assert.True(index.SiblingCounts.TryGetTotal(0, KeyOf("item"), out long total));
        Assert.Equal(500, total);
    }

    [Fact]
    public void TextAndCDataAreCountedTogetherAndInstructionsByTarget()
    {
        Assert.NotEqual(XmlSiblingCounts.TextKey, XmlSiblingCounts.CommentKey);
        Assert.NotEqual(XmlSiblingCounts.ProcessingInstructionKeyOf("a"u8), XmlSiblingCounts.ProcessingInstructionKeyOf("b"u8));
        Assert.NotEqual(XmlSiblingCounts.KeyOf("a"u8), XmlSiblingCounts.ProcessingInstructionKeyOf("a"u8));

        string text = "<root>t1<x/><![CDATA[c]]><x/>t2<?a 1?><?b 2?><?a 3?><!--c--></root>";
        var index = Build(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), promotionBytes: 1, checkpointBytes: 1);

        Assert.True(index.SiblingCounts.TryGetTotal(0, XmlSiblingCounts.TextKey, out long texts));
        Assert.Equal(3, texts);
        Assert.True(index.SiblingCounts.TryGetTotal(0, XmlSiblingCounts.ProcessingInstructionKeyOf("a"u8), out long a));
        Assert.Equal(2, a);
        Assert.True(index.SiblingCounts.TryGetTotal(0, XmlSiblingCounts.CommentKey, out long comments));
        Assert.Equal(1, comments);
        Assert.True(index.SiblingCounts.TryGetTotal(0, XmlSiblingCounts.KeyOf("x"u8), out long xs));
        Assert.Equal(2, xs);
    }

    [Fact]
    public async Task AKeptStructureReopensWithoutAScan()
    {
        byte[] xml = RandomXml.LargeContainers(new Random(11), elements: 20);
        var model = XmlModel.Parse(xml);
        long expectedGroups = model.Single(n => n.Depth == 0 && n.Kind == XmlNodeKind.Element).Children.Count(c => model[c].Key == "group");
        var first = Build(new MemoryByteSource(xml));
        var kept = first.DetachStructure();
        Assert.NotNull(kept);

        var reopened = XmlSparseIndex.StartIndexing(new MemoryByteSource(xml), kept);
        await reopened.IndexingTask;

        Assert.True(reopened.AllItemsPublished);
        Assert.Equal(Snapshot(first), Snapshot(reopened));
        Assert.True(reopened.SiblingCounts.TryGetTotal(0, KeyOf("group"), out long groups));
        Assert.Equal(expectedGroups, groups);
        Assert.Throws<ArgumentException>(() => XmlSparseIndex.Reopen(new MemoryByteSource(xml[..^1]), kept!));
    }

    [Fact]
    public void AFailedScanKeepsNoStructure()
    {
        var index = XmlSparseIndex.StartIndexing(new MemoryByteSource("<a><b></a>"u8.ToArray()), promotionBytes: 1, checkpointBytes: 1);
        Assert.ThrowsAny<Exception>(() => index.IndexingTask.GetAwaiter().GetResult());

        Assert.Null(index.DetachStructure());
    }

    [Fact]
    public async Task TheScan_HintsTheBytesAheadOfIt()
    {
        var source = new PrefetchRecordingSource(Encoding.UTF8.GetBytes("<root>" + string.Concat(Enumerable.Repeat("<a>1</a>", 1000)) + "</root>"));
        var index = XmlSparseIndex.StartIndexing(source, Promotion, Checkpoint);
        await index.IndexingTask;

        Assert.Equal(0, source.Hints[0].Offset);
    }

    private static ulong KeyOf(string modelKey) => modelKey switch
    {
        "#text" => XmlSiblingCounts.TextKey,
        "#comment" => XmlSiblingCounts.CommentKey,
        _ when modelKey.StartsWith("#pi:", StringComparison.Ordinal) => XmlSiblingCounts.ProcessingInstructionKeyOf(Encoding.UTF8.GetBytes(modelKey[4..])),
        _ => XmlSiblingCounts.KeyOf(Encoding.UTF8.GetBytes(modelKey)),
    };

    private static string Describe(XmlModel.Node c) =>
        $"{c.Start}-{c.End} depth {c.Depth} #{c.Ordinal} children {c.Children.Count} kind {(byte)c.Kind}";

    private static string Describe(TreeContainer c) =>
        $"{c.Start}-{c.End} depth {c.Depth} #{c.OrdinalInParent} children {c.ChildCount} kind {c.FormatKind}";

    private static IEnumerable<TreeContainer> Recorded(SparseContainerIndex structure) =>
        Enumerable.Range(0, structure.ContainerCount).Select(structure.GetContainer);

    private static XmlSparseIndex Build(IByteSource source, int promotionBytes = Promotion, int checkpointBytes = Checkpoint)
    {
        var index = XmlSparseIndex.StartIndexing(source, promotionBytes, checkpointBytes);
        index.IndexingTask.GetAwaiter().GetResult();
        return index;
    }

    /// <summary>The whole index as lines, so a mismatch reports the first line that differs.</summary>
    private static List<string> Snapshot(XmlSparseIndex index) =>
        Recorded(index.Structure).Select(c => c.ToString())
            .Concat(Enumerable.Range(0, index.Structure.CheckpointCount).Select(i => index.Structure.GetCheckpoint(i).ToString()))
            .ToList();
}
