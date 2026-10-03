using Argonaut.Features.Raw.Highlighting;

namespace Argonaut.Tests.Features.Raw.Highlighting;

public sealed class RawLexerChoiceTests
{
    [Theory]
    [InlineData("a.json")]
    [InlineData("/x/y/A.JSONC")]
    [InlineData("a.json5")]
    [InlineData("a.geojson")]
    [InlineData("a.ndjson")]
    [InlineData("a.jsonl")]
    public void JsonExtensions_ChooseJson(string path)
        => Assert.Same(RawLexerChoice.Json, RawLexerChoice.ForPath(path));

    [Theory]
    [InlineData("a.ini")]
    [InlineData("a.cfg")]
    [InlineData("a.conf")]
    [InlineData("a.properties")]
    [InlineData("a.toml")]
    [InlineData("/home/me/.editorconfig")]
    [InlineData("/home/me/.gitconfig")]
    [InlineData(".env")]
    [InlineData("/app/.env.local")]
    [InlineData("/app/.ENV.production")]
    [InlineData("a.yaml")]
    [InlineData("a.YML")]
    public void ConfigNames_ChooseConfig(string path)
        => Assert.Same(RawLexerChoice.Config, RawLexerChoice.ForPath(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notes.txt")]
    [InlineData("environment")]
    [InlineData("a.envx")]
    public void OtherNames_ChooseNothing(string? path)
        => Assert.Null(RawLexerChoice.ForPath(path));

    [Theory]
    [InlineData("{\"a\": 1}")]
    [InlineData("  {")]
    [InlineData("[{\"a\": 1}]")]
    [InlineData("[[1, 2]]")]
    [InlineData("[\"a\", \"b\"]")]
    [InlineData("[1, 2, 3]")]
    [InlineData("[]")]
    [InlineData("[")]
    public void FirstLineOpeningAJsonValue_IsJson(string first)
        => Assert.Same(RawLexerChoice.Json, RawLexerChoice.Sniff(new[] { first, "x = 1" }));

    [Fact]
    public void SectionHeader_IsNotJson()
    {
        var chosen = RawLexerChoice.Sniff(new[] { "[core]", "a = 1", "b = 2" });
        Assert.Same(RawLexerChoice.Config, chosen);
    }

    [Fact]
    public void SectionsAndCommentsAreNotCounted()
    {
        var chosen = RawLexerChoice.Sniff(new[]
        {
            "; settings", "[a]", "x = 1", "", "[b]", "y = 2", "# note", "[c]", "z = 3",
        });
        Assert.Same(RawLexerChoice.Config, chosen);
    }

    [Fact]
    public void MostLinesLikeKeyEqualsValue_IsConfig()
    {
        var lines = new[] { "a=1", "b = 2", "c = three", "just a stray line", "d=4" };
        Assert.Same(RawLexerChoice.Config, RawLexerChoice.Sniff(lines));
    }

    [Fact]
    public void MostLinesLikeKeyColonValue_IsConfig()
    {
        var lines = new[] { "name: x", "items:", "  - id: 1", "  - id: 2", "enabled: true" };
        Assert.Same(RawLexerChoice.Config, RawLexerChoice.Sniff(lines));
    }

    [Fact]
    public void MixedSeparators_CountTogether()
        => Assert.Same(RawLexerChoice.Config, RawLexerChoice.Sniff(new[] { "a = 1", "b: 2", "c=3", "prose here" }));

    [Fact]
    public void ColonsWithoutABlankAfterThem_AreNotPairs()
        => Assert.Null(RawLexerChoice.Sniff(new[] { "http://x.org", "C:\\dir", "12:30" }));

    [Fact]
    public void ProseAndEmptyInput_ChooseNothing()
    {
        Assert.Null(RawLexerChoice.Sniff(new[] { "Dear Sir,", "", "I write to say hello.", "Yours" }));
        Assert.Null(RawLexerChoice.Sniff(Array.Empty<string>()));
        Assert.Null(RawLexerChoice.Sniff(new[] { "", "   " }));
    }

    [Fact]
    public void OnlyTheFirstFiftyLinesAreRead()
    {
        var lines = Enumerable.Repeat("prose line", 50).Concat(Enumerable.Repeat("a = 1", 500)).ToList();
        Assert.Null(RawLexerChoice.Sniff(lines));
    }

    [Fact]
    public void For_ResolvesEachPickerEntry()
    {
        var auto = RawLexerChoice.Config;
        Assert.Same(auto, RawLexerChoice.For(RawColourChoice.Auto, auto));
        Assert.Null(RawLexerChoice.For(RawColourChoice.Auto, null));
        Assert.Null(RawLexerChoice.For(RawColourChoice.Off, auto));
        Assert.Same(RawLexerChoice.Json, RawLexerChoice.For(RawColourChoice.Json, auto));
        Assert.Same(RawLexerChoice.Config, RawLexerChoice.For(RawColourChoice.Config, null));
    }
}
