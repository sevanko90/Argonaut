using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Xml.Tree;

/// <summary>
/// XML through <see cref="TreeCursor"/>: every row, both directions, and seeks, against the rows
/// <see cref="XmlModel"/> gives for the same expansion - and the one invariant the index and the
/// reader share, which of the elements are containers.
/// </summary>
public class XmlTreeReaderTests
{
    public static TheoryData<int, int> Cases => new()
    {
        { 1, 1 }, { 2, 3 }, { 3, 2 }, { 4, 9 }, { 5, 0 }, { 6, 1 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ForwardBackwardAndSeekMatchTheModel(int seed, int defaultDepth)
    {
        var random = new Random(seed);
        byte[] xml = RandomXml.LargeContainers(random, elements: 20, wideChildren: 6);
        AssertCursorMatchesModel(new MemoryByteSource(xml), xml, random, defaultDepth, seekStride: 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ASplitSourceReadsTheSameRows(int seed)
    {
        var random = new Random(seed);
        byte[] xml = RandomXml.Document(random, elements: 30);
        AssertCursorMatchesModel(new SplitByteSource(xml, 1), xml, random, defaultDepth: 9, seekStride: 7);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheIndexAndTheReaderAgreeOnWhichElementsAreContainers(int seed, int _)
    {
        byte[] xml = RandomXml.LargeContainers(new Random(seed), elements: 20, wideChildren: 6);
        var model = XmlModel.Parse(xml);
        var source = new MemoryByteSource(xml);
        var index = XmlSparseIndex.StartIndexing(source, promotionBytes: 1, checkpointBytes: 1);
        index.IndexingTask.GetAwaiter().GetResult();

        // At promotion 1 every container is recorded, so the index's containers are exactly the
        // elements the scanner decided were containers.
        var indexed = Enumerable.Range(0, index.Structure.ContainerCount).Select(c => index.Structure.GetContainer(c).Start).ToHashSet();

        var cursor = new TreeCursor(index.Structure, new XmlTreeReader(source), new TreeExpandState(int.MaxValue));
        var read = new Dictionary<long, (bool IsContainer, byte Kind)>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
        {
            if (cursor.Current.Shape != TreeRowShape.Close)
                read[cursor.Current.Node.ValueStart] = (cursor.Current.Node.IsContainer, cursor.Current.Node.FormatKind);
        }

        foreach (var node in model.Where(n => n.Kind is XmlNodeKind.Element or XmlNodeKind.TextElement or XmlNodeKind.EmptyElement))
        {
            bool container = node.Kind == XmlNodeKind.Element;
            Assert.Equal(container, indexed.Contains(node.Start));
            Assert.Equal((container, (byte)node.Kind), read[node.Start]);
        }
    }

    [Fact]
    public void TheDocumentLevelHoldsTheProlog()
    {
        byte[] xml = "<?xml version=\"1.0\"?>\n<!-- c -->\n<!DOCTYPE r [ <!ENTITY e \"]>\"> ]>\n<?pi x?>\n<r><a/></r>\n<!-- end -->\n"u8.ToArray();
        var source = new MemoryByteSource(xml);
        var index = XmlSparseIndex.StartIndexing(source);
        index.IndexingTask.GetAwaiter().GetResult();
        var cursor = new TreeCursor(index.Structure, new XmlTreeReader(source), new TreeExpandState(0));

        var kinds = new List<XmlNodeKind>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            kinds.Add((XmlNodeKind)cursor.Current.Node.FormatKind);

        Assert.Equal([XmlNodeKind.XmlDeclaration, XmlNodeKind.Comment, XmlNodeKind.Doctype, XmlNodeKind.ProcessingInstruction,
            XmlNodeKind.Element, XmlNodeKind.Comment], kinds);
        Assert.Equal((byte)XmlNodeKind.Document, new XmlTreeReader(source).DocumentKind);
    }

    [Fact]
    public void SkippingAnElementFindsItsEndThroughEverythingThatHidesATag()
    {
        byte[] xml = "<a x=\"</a>\" y='>'><!-- </a> --><![CDATA[</a>]]><?p </a>?><a>text</a><b/></a>tail"u8.ToArray();
        var reader = new XmlTreeReader(new MemoryByteSource(xml));

        Assert.Equal(xml.Length - 4, reader.SkipValue(0));
        Assert.Equal("<a x=\"</a>\" y='>'>".Length, reader.FirstChildPosition(0));
        Assert.Equal(xml.Length - 8, reader.CloseStart(0, xml.Length - 4));
    }

    [Fact]
    public void AnElementStillArrivingRunsPastWhatHasArrived()
    {
        byte[] xml = "<a><b>1</b><c>"u8.ToArray();
        var growing = new GrowingByteSource(xml, initiallyAvailable: xml.Length);

        Assert.Equal(long.MaxValue, new XmlTreeReader(growing).SkipValue(0));
    }

    private static void AssertCursorMatchesModel(IByteSource source, byte[] xml, Random random, int defaultDepth, int seekStride)
    {
        var model = XmlModel.Parse(xml);
        var index = XmlSparseIndex.StartIndexing(source, promotionBytes: 256, checkpointBytes: 64);
        index.IndexingTask.GetAwaiter().GetResult();

        var expand = new TreeExpandState(defaultDepth);
        foreach (var node in XmlModel.Containers(model))
        {
            if (random.Next(5) == 0)
                expand.Toggle(node.Start);
        }

        var modelRows = XmlModel.Rows(model, expand);
        var rows = modelRows.Select(r => Describe(model, r)).ToList();
        var cursor = new TreeCursor(index.Structure, new XmlTreeReader(source), expand);

        var forward = new List<Row>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
            forward.Add(Describe(cursor.Current));
        Assert.Equal(rows, forward);

        var backward = new List<Row>();
        for (bool more = cursor.MoveToEnd(); more; more = cursor.MovePrevious())
            backward.Add(Describe(cursor.Current));
        backward.Reverse();
        Assert.Equal(rows, backward);

        for (long offset = 0; offset < xml.Length; offset += seekStride)
        {
            Assert.True(cursor.SeekTo(offset));
            int expected = XmlModel.RowShowing(offset, model, expand, modelRows);
            Assert.Equal(rows[expected], Describe(cursor.Current));

            if (expected > 0)
            {
                Assert.True(cursor.MovePrevious());
                Assert.Equal(rows[expected - 1], Describe(cursor.Current));
            }
        }
    }

    private readonly record struct Row(long ValueStart, bool IsClose, long Start, int Depth, long Ordinal, byte Kind);

    private static Row Describe(TreeRow row) =>
        new(row.Node.ValueStart, row.Shape == TreeRowShape.Close, row.Start, row.Depth, row.Ordinal, row.Node.FormatKind);

    private static Row Describe(List<XmlModel.Node> model, XmlModel.Row row)
    {
        var node = model[row.Node];
        return new Row(node.Start, row.IsClose, row.IsClose ? node.CloseStart : node.Start, node.Depth, node.Ordinal, (byte)node.Kind);
    }
}
