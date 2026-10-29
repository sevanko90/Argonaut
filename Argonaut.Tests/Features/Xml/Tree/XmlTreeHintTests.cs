using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Hints;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Features.Xml.Tree;
using Argonaut.Ui.Hints;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Xml.Tree;

/// <summary>
/// Value hints on XML text: the content of a text-only element and a text node in mixed content
/// get the JSON tree's hints - dates, addresses, colours, tokens - classified by shape as always,
/// from the text with its entity and character references decoded, so an address written
/// <c>?a=1&amp;amp;b=2</c> opens as <c>?a=1&amp;b=2</c>. Names, attributes, comments and the like
/// get none.
/// </summary>
public sealed class XmlTreeHintTests
{
    [Fact]
    public void AnIsoDateGetsADateChip()
    {
        var row = Row("<r><core:creationDate>2020-12-02</core:creationDate></r>", "<core:creationDate>");

        Assert.Contains(row, r => r.Style == TreeRunStyle.Chip && r.Icon == TreeRunIcon.Time);
    }

    [Fact]
    public void AnAddressIsALinkThatOpensIt()
    {
        var row = Row("<r><core:informationSystem>https://www.berlin.de/sen/</core:informationSystem></r>", "<core:informationSystem>");

        var value = Assert.Single(row, r => r.Style == TreeRunStyle.String);
        Assert.Equal(new Uri("https://www.berlin.de/sen/"), Assert.IsType<OpenUrlLink>(value.Link).Address);
    }

    [Fact]
    public void AnAddressIsReadWithItsEntitiesDecoded()
    {
        var row = Row("<r><v>https://registry.example.org/p/1?view=full&amp;lang=de&#38;x=&#x31;</v></r>", "<v>");

        var value = Assert.Single(row, r => r.Style == TreeRunStyle.String);
        Assert.Equal("https://registry.example.org/p/1?view=full&amp;lang=de&#38;x=&#x31;", value.Text);
        Assert.Equal(new Uri("https://registry.example.org/p/1?view=full&lang=de&x=1"), Assert.IsType<OpenUrlLink>(value.Link).Address);
    }

    [Fact]
    public void AColourGetsASwatch()
    {
        var row = Row("<r><gen:value>#8B4513</gen:value></r>", "<gen:value>");

        Assert.Contains(row, r => r.Style == TreeRunStyle.Chip && r.Swatch is not null);
    }

    [Fact]
    public void ATextNodeInMixedContentIsHintedToo()
    {
        var row = Row("<p><b/>https://example.org/docs</p>", "https://");

        Assert.IsType<OpenUrlLink>(Assert.Single(row, r => r.Style == TreeRunStyle.String).Link);
    }

    [Fact]
    public void ATokenGetsAChipThatExpands()
    {
        string jwt = Jwt();
        var row = Row($"<r><token>{jwt}</token></r>", "<token>");

        var chip = Assert.Single(row, r => r.Style == TreeRunStyle.Chip);
        var link = Assert.IsType<ExpandHintLink>(chip.Link);
        string xml = $"<r><token>{jwt}</token></r>";
        Assert.Equal(xml.IndexOf(jwt, StringComparison.Ordinal), link.ValueStart);
        Assert.Equal(xml.IndexOf(jwt, StringComparison.Ordinal) + jwt.Length, link.ValueEnd);
    }

    [Theory]
    [InlineData("<r><a href=\"https://example.org\">plain</a></r>", "<a ")]
    [InlineData("<r><!-- https://example.org --></r>", "<!--")]
    [InlineData("<r><https.example.org/></r>", "<https")]
    [InlineData("<r><?pi https://example.org?></r>", "<?pi")]
    public void NamesAttributesCommentsAndInstructionsAreNotHinted(string xml, string rowStartsWith)
    {
        var row = Row(xml, rowStartsWith);

        Assert.DoesNotContain(row, r => r.Style == TreeRunStyle.Chip || r.Link is OpenUrlLink);
    }

    [Fact]
    public void WithoutProvidersThereAreNoHints()
    {
        var row = Row("<r><v>https://example.org</v></r>", "<v>", hinted: false);

        Assert.DoesNotContain(row, r => r.Style == TreeRunStyle.Chip || r.Link is not null);
    }

    private static string Jwt()
    {
        static string Part(string json) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        return $"{Part("""{"alg":"HS256"}""")}.{Part("""{"sub":"u1","exp":2000000000}""")}.c2ln";
    }

    /// <summary>The runs of the row starting with <paramref name="rowStartsWith"/>, everything expanded.</summary>
    private static List<TreeRun> Row(string xml, string rowStartsWith, bool hinted = true)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        var source = new MemoryByteSource(bytes);
        var index = XmlSparseIndex.StartIndexing(source);
        index.IndexingTask.GetAwaiter().GetResult();
        var reader = new XmlTreeReader(source);
        var providers = hinted ? ValueHintProviders.Standard(new DateHintSettings(), TimeProvider.System) : null;
        var painter = new XmlTreePainter(new XmlTreeText(source, index.Structure, reader), providers);

        long start = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(rowStartsWith));
        var cursor = new TreeCursor(index.Structure, reader, new TreeExpandState(int.MaxValue));
        Assert.True(cursor.SeekTo(start));
        Assert.Equal(start, cursor.Current.Start);

        var runs = new List<TreeRun>();
        painter.AppendRuns(cursor.Current, runs);
        return runs;
    }
}
