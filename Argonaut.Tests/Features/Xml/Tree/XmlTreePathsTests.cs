using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Tests.Support;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Xml.Tree;

/// <summary>
/// XPaths in the XML tree, spelled as written (<c>docs/xml-view-options.md</c>, "XPaths"): each
/// step is the name as the document writes it, or <c>text()</c>, <c>comment()</c> or
/// <c>processing-instruction('target')</c>, numbered <c>[k]</c> among siblings of the same name
/// or kind - and left unnumbered when it is the only one. Every row's path resolves back to it,
/// through recorded containers and their checkpoints' counts. The XML declaration and a DOCTYPE
/// are not XPath nodes; their path is <c>/</c>, which resolves to the document's first row.
/// </summary>
public sealed class XmlTreePathsTests
{
    private const string Sample = """
        <?xml version="1.0"?>
        <!-- top -->
        <core:CityModel xmlns:core="urn:c" xmlns:bldg="urn:b">
          <core:member><bldg:Building id="1"/></core:member>
          <core:member>
            <Building xmlns="urn:b"><name>x</name></Building>
          </core:member>
          text one
          <!-- c1 -->
          <?pi a?>
          <![CDATA[cd]]>
          <?pi b?>
          <!-- c2 -->
          <core:member><bldg:Building/></core:member>
        </core:CityModel>
        """;

    [Theory]
    [InlineData("<?xml", "/")]
    [InlineData("<!-- top", "/comment()")]
    [InlineData("<core:CityModel", "/core:CityModel")]
    [InlineData("<core:member><bldg:Building id", "/core:CityModel/core:member[1]")]
    [InlineData("<bldg:Building id", "/core:CityModel/core:member[1]/bldg:Building")]
    [InlineData("<Building xmlns", "/core:CityModel/core:member[2]/Building")]
    [InlineData("<name>", "/core:CityModel/core:member[2]/Building/name")]
    [InlineData("text one", "/core:CityModel/text()[1]")]
    [InlineData("<![CDATA[", "/core:CityModel/text()[2]")]
    [InlineData("<!-- c1", "/core:CityModel/comment()[1]")]
    [InlineData("<!-- c2", "/core:CityModel/comment()[2]")]
    [InlineData("<?pi a", "/core:CityModel/processing-instruction('pi')[1]")]
    [InlineData("<?pi b", "/core:CityModel/processing-instruction('pi')[2]")]
    [InlineData("<core:member><bldg:Building/>", "/core:CityModel/core:member[3]")]
    [InlineData("<bldg:Building/>", "/core:CityModel/core:member[3]/bldg:Building")]
    public void ARowsPathIsSpelledAsTheDocumentWritesIt(string rowStartsWith, string path)
    {
        var tree = new Harness(Sample);
        long start = tree.Offset(rowStartsWith);

        var segments = tree.SegmentsAt(start);

        Assert.Equal(path, XmlTreePaths.Format(segments));
        Assert.Equal(start, segments[^1].Target);
        Assert.Equal(start, tree.Resolve(path).Target);
    }

    [Fact]
    public void EachSegmentNamesItsOwnRow()
    {
        var tree = new Harness(Sample);
        var segments = tree.SegmentsAt(tree.Offset("<name>"));

        Assert.Equal(["/core:CityModel", "/core:member[2]", "/Building", "/name"], segments.Select(s => s.Label).ToList());
        Assert.Equal(
            [tree.Offset("<core:CityModel"), tree.Offset("<core:member>\n"), tree.Offset("<Building xmlns"), tree.Offset("<name>")],
            segments.Select(s => s.Target).ToList());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryRowsPathIsTheModelsAndResolvesBackToIt(int seed)
    {
        byte[] xml = RandomXml.LargeContainers(new Random(seed), elements: 30, wideChildren: 10);
        AssertEveryPathRoundTrips(xml);
    }

    [Fact]
    public void AContainerPastTheDistinctNameCapIsCountedByReading()
    {
        // 17 names, repeated: the index keeps no counts for the root, so k is found by reading.
        var text = new StringBuilder("<root>");
        for (int i = 0; i < 1500; i++)
            text.Append($"<n{i % 17}>{i}</n{i % 17}>");
        AssertEveryPathRoundTrips(Encoding.UTF8.GetBytes(text.Append("</root>").ToString()));
    }

    [Fact]
    public async Task WhileARecordedContainerIsStillIndexing_EveryStepIsNumbered()
    {
        byte[] xml = Encoding.UTF8.GetBytes("<root><only/>" + string.Concat(Enumerable.Range(0, 2000).Select(i => $"<item>{i}</item>")) + "</root>");
        var growing = new GrowingByteSource(xml, initiallyAvailable: xml.Length / 2);
        var tree = new Harness(growing, xml);
        SpinWait.SpinUntil(() => tree.Index.Structure.ContainerCount > 0 && tree.Index.Structure.ScannedTo > xml.Length / 4, TimeSpan.FromSeconds(5));

        Assert.Equal("/root/only[1]", XmlTreePaths.Format(tree.SegmentsAt(tree.Offset("<only/>"))));

        growing.Reveal(xml.Length);
        growing.Seal();
        await tree.Index.IndexingTask;
        Assert.Equal("/root/only", XmlTreePaths.Format(tree.SegmentsAt(tree.Offset("<only/>"))));
        Assert.Equal("/root/item[2000]", XmlTreePaths.Format(tree.SegmentsAt(tree.Offset("<item>1999<"))));
    }

    [Fact]
    public void AStarStepCountsElementsOfAnyName()
    {
        var tree = new Harness(Sample);

        Assert.Equal(tree.Offset("<core:member>\n"), tree.Resolve("/core:CityModel/*[2]").Target);
        Assert.Equal(tree.Offset("<Building xmlns"), tree.Resolve("/*/*[2]/*").Target);
    }

    [Theory]
    [InlineData("/core:CityModel/core:missing", "core:missing")]
    [InlineData("/core:CityModel/core:member[9]", "core:member[9]")]
    [InlineData("/core:CityModel/comment()[3]", "comment()[3]")]
    [InlineData("/other", "other")]
    public void APathThatLeadsNowhereSaysWhichStep(string path, string step)
    {
        var result = new Harness(Sample).Resolve(path);

        Assert.Null(result.Target);
        Assert.Contains(step, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("core:CityModel")]
    [InlineData("//core:member")]
    [InlineData("/core:CityModel/child::core:member")]
    [InlineData("/core:CityModel/core:member[@id='1']")]
    [InlineData("/core:CityModel/core:member[0]")]
    [InlineData("/core:CityModel/core:member[last()]")]
    [InlineData("/core:CityModel/core:member[2")]
    [InlineData("/core:CityModel/..")]
    public void UnsupportedSyntaxIsRefusedNamingWhatIsSupported(string path)
    {
        var result = new Harness(Sample).Resolve(path);

        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
        Assert.Contains("supported", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertEveryPathRoundTrips(byte[] xml)
    {
        var model = XmlModel.Parse(xml);
        var tree = new Harness(new MemoryByteSource(xml), xml, promotionBytes: 256, checkpointBytes: 64);
        var cursor = tree.Cursor();

        int compared = 0;
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
        {
            if (cursor.Current.Shape == TreeRowShape.Close)
                continue;

            int node = model.FindIndex(n => n.Start == cursor.Current.Start);
            string expected = ModelPath(model, node);
            var segments = XmlTreePaths.Segments(cursor, tree.Text, tree.Index);
            Assert.Equal(expected, XmlTreePaths.Format(segments));

            // The declaration and a DOCTYPE are not XPath nodes: both spell "/", which resolves to
            // the document's first row.
            if (expected != "/")
                Assert.Equal(cursor.Current.Start, tree.Resolve(expected).Target);
            compared++;
        }

        Assert.True(compared > 100);
    }

    /// <summary>The path the plan spells for a model node.</summary>
    private static string ModelPath(List<XmlModel.Node> model, int node)
    {
        if (model[node].Kind is XmlNodeKind.XmlDeclaration or XmlNodeKind.Doctype)
            return "/";

        var steps = new List<string>();
        for (int at = node; at >= 0; at = model[at].Parent)
        {
            var current = model[at];
            var siblings = (current.Parent < 0 ? XmlModel.TopLevel(model) : model[current.Parent].Children)
                .Where(s => model[s].Key == current.Key).ToList();
            string name = current.Key switch
            {
                "#text" => "text()",
                "#comment" => "comment()",
                _ when current.Key.StartsWith("#pi:", StringComparison.Ordinal) => $"processing-instruction('{current.Key[4..]}')",
                _ => current.Key,
            };
            steps.Add(siblings.Count == 1 ? name : $"{name}[{siblings.IndexOf(at) + 1}]");
        }

        steps.Reverse();
        return "/" + string.Join('/', steps);
    }

    private sealed class Harness
    {
        private readonly byte[] bytes;

        public Harness(string xml) : this(new MemoryByteSource(Encoding.UTF8.GetBytes(xml)), Encoding.UTF8.GetBytes(xml), promotionBytes: 1, checkpointBytes: 1)
        {
            Index.IndexingTask.GetAwaiter().GetResult();
        }

        public Harness(IByteSource source, byte[] bytes, int promotionBytes = 64, int checkpointBytes = 16)
        {
            this.bytes = bytes;
            Index = XmlSparseIndex.StartIndexing(source, promotionBytes, checkpointBytes);
            if (source.LengthSettled)
                Index.IndexingTask.GetAwaiter().GetResult();
            Reader = new XmlTreeReader(source);
            Text = new XmlTreeText(source, Index.Structure, Reader);
        }

        public XmlSparseIndex Index { get; }
        public XmlTreeReader Reader { get; }
        public XmlTreeText Text { get; }

        public TreeCursor Cursor() => new(Index.Structure, Reader, new TreeExpandState(int.MaxValue));

        public long Offset(string startsWith)
        {
            int at = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(startsWith));
            Assert.True(at >= 0, $"'{startsWith}' is not in the document");
            return at;
        }

        public IReadOnlyList<TreePathSegment> SegmentsAt(long offset)
        {
            var cursor = Cursor();
            Assert.True(cursor.SeekTo(offset));
            Assert.Equal(offset, cursor.Current.Start);
            return XmlTreePaths.Segments(cursor, Text, Index);
        }

        public TreePathResult Resolve(string path) => XmlTreePaths.Resolve(Index, Reader, Text, path);
    }
}
