using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Xml.Tree;

/// <summary>
/// What an XML tree row says. Markup is shown as written - quotes, entity references and
/// prefixes kept - except that a run of whitespace holding a line break or tab shows as one
/// space, since a row is one line. Which colours the runs take is settled from screenshots
/// (plan step 7); these tests pin only the roles the painter gives them.
/// </summary>
public sealed class XmlTreePainterTests
{
    private const string Sample = """
        <?xml version="1.0"?>
        <!-- top -->
        <root a="1" b='two'
              c="x &gt; y">
          <name>Foo &amp; Bar</name>
          <empty/>
          <blank>   </blank>
          <pair></pair>
          text
             run
          <![CDATA[ raw <cdata> ]]>
          <?pi some data?>
          <!-- line one
               line two -->
          <child><leaf>1</leaf><leaf>2</leaf></child>
          <ñame>日本</ñame>
        </root>
        """;

    [Fact]
    public void EveryRowSaysWhatTheDocumentSays()
    {
        var rows = Rows(Sample, defaultDepth: int.MaxValue);

        Assert.Equal(
        [
            "<?xml version=\"1.0\"?>",
            "<!-- top -->",
            "<root a=\"1\" b='two' c=\"x &gt; y\">",
            "<name>Foo &amp; Bar</name>",
            "<empty/>",
            "<blank></blank>",
            "<pair></pair>",
            "text run",
            "<![CDATA[ raw <cdata> ]]>",
            "<?pi some data?>",
            "<!-- line one line two -->",
            "<child>",
            "<leaf>1</leaf>",
            "<leaf>2</leaf>",
            "</child>",
            "<ñame>日本</ñame>",
            "</root>",
        ], rows.Select(r => r.Shown).ToList());
    }

    [Fact]
    public void ACollapsedElementShowsItsStartTagItsChildCountAndItsCloseTag()
    {
        var child = Rows(Sample, defaultDepth: 1).Single(r => r.Shown.StartsWith("<child>", StringComparison.Ordinal));

        Assert.EndsWith("</child>", child.Shown);
        Assert.Contains(child.Runs, r => r.Text.Contains("2 children", StringComparison.Ordinal));
        Assert.True(child.Row.Node.IsContainer);
        Assert.False(child.Row.IsExpanded);
    }

    [Fact]
    public void RunsCarryTheRoleOfWhatTheyShow()
    {
        var rows = Rows(Sample, defaultDepth: int.MaxValue);

        var root = rows.Single(r => r.Shown.StartsWith("<root", StringComparison.Ordinal));
        Assert.Contains(root.Runs, r => r.Style == TreeRunStyle.Name && r.Text == "root");
        Assert.Contains(root.Runs, r => r.Style == TreeRunStyle.AttributeName && r.Text == "b");
        Assert.Contains(root.Runs, r => r.Style == TreeRunStyle.AttributeValue && r.Text.Contains("two", StringComparison.Ordinal));

        var name = rows.Single(r => r.Shown.StartsWith("<name>", StringComparison.Ordinal));
        Assert.Contains(name.Runs, r => r.Style == TreeRunStyle.String && r.Text == "Foo &amp; Bar");

        Assert.All(rows.Single(r => r.Shown == "<!-- top -->").Runs, r => Assert.Equal(TreeRunStyle.Comment, r.Style));
        Assert.Contains(rows.Single(r => r.Shown == "text run").Runs, r => r.Style == TreeRunStyle.String);
        Assert.Null(new XmlTreePainter(Text(Sample)).Marker(rows[0].Row));
    }

    [Fact]
    public void ALongTextIsCappedWithItsSizeAndAWayToTheRawView()
    {
        string coordinates = string.Join(' ', Enumerable.Range(0, 20_000).Select(i => $"{i}.125"));
        var row = Rows($"<root><posList>{coordinates}</posList></root>", defaultDepth: int.MaxValue)
            .Single(r => r.Shown.StartsWith("<posList>", StringComparison.Ordinal));

        Assert.Contains("…", row.Shown);
        Assert.True(row.Shown.Length < DisplayText.MaxLength + 200, $"a capped row, not {row.Shown.Length} characters");
        Assert.Contains(row.Runs, r => r.Style == TreeRunStyle.Note);
        var action = Assert.Single(row.Runs, r => r.Style == TreeRunStyle.Action);
        Assert.Equal("Open in raw", action.Text);
        Assert.NotNull(action.Link);
    }

    [Fact]
    public void AStartTagPastTheCapIsCappedToo()
    {
        string attributes = string.Concat(Enumerable.Range(0, 400).Select(i => $" a{i}=\"{i}\""));
        var row = Rows($"<root{attributes}><a/></root>", defaultDepth: int.MaxValue)[0];

        Assert.Contains("…", row.Shown);
        Assert.True(row.Shown.Length < DisplayText.MaxLength + 200);
    }

    [Fact]
    public void AttributeValuesHoldingAnglesAndTheOtherQuoteAreShownWhole()
    {
        var row = Rows("<a x=\"1 > 0\" y='say \"hi\"' z=\"it's\"/>", defaultDepth: 1)[0];

        Assert.Equal("<a x=\"1 > 0\" y='say \"hi\"' z=\"it's\"/>", row.Shown);
    }

    private sealed record PaintedRow(TreeRow Row, List<TreeRun> Runs)
    {
        /// <summary>The row's text, without the notes and actions laid out after it.</summary>
        public string Shown => string.Concat(Runs.Where(r => r.Style is not (TreeRunStyle.Note or TreeRunStyle.Chip or TreeRunStyle.Action)).Select(r => r.Text));
    }

    private static XmlTreeText Text(string xml)
    {
        var source = new MemoryByteSource(Encoding.UTF8.GetBytes(xml));
        var index = XmlSparseIndex.StartIndexing(source);
        index.IndexingTask.GetAwaiter().GetResult();
        return new XmlTreeText(source, index.Structure, new XmlTreeReader(source));
    }

    private static List<PaintedRow> Rows(string xml, int defaultDepth)
    {
        var source = new MemoryByteSource(Encoding.UTF8.GetBytes(xml));
        var index = XmlSparseIndex.StartIndexing(source);
        index.IndexingTask.GetAwaiter().GetResult();
        var reader = new XmlTreeReader(source);
        var painter = new XmlTreePainter(new XmlTreeText(source, index.Structure, reader));
        var cursor = new TreeCursor(index.Structure, reader, new TreeExpandState(defaultDepth));

        var rows = new List<PaintedRow>();
        for (bool more = cursor.MoveToStart(); more; more = cursor.MoveNext())
        {
            var runs = new List<TreeRun>();
            painter.AppendRuns(cursor.Current, runs);
            rows.Add(new PaintedRow(cursor.Current, runs));
        }

        return rows;
    }
}
