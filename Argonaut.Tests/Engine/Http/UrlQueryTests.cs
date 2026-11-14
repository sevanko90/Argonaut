using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Http;

/// <summary>
/// A URL's query as an editable list of arguments: parsed from whatever the user typed or pasted,
/// and written back so that only what they edited changes.
/// </summary>
public sealed class UrlQueryTests
{
    private static string[] Shown(UrlQuery query) => query.Arguments.Select(a => a.Value is null ? a.Key : $"{a.Key}={a.Value}").ToArray();

    [Fact]
    public void Parsing_SplitsBaseArgumentsAndFragment()
    {
        var query = UrlQuery.Parse("https://api.example.com/items?page=2&size=50#top");

        Assert.Equal("https://api.example.com/items", query.Base);
        Assert.Equal(["page=2", "size=50"], Shown(query));
        Assert.Equal("top", query.Fragment);
    }

    [Fact]
    public void ArgumentsAreShownDecoded()
    {
        var query = UrlQuery.Parse("https://x/?q=caf%C3%A9%20au+lait&tag=a%26b&eq=1%3D1");

        Assert.Equal(["q=café au lait", "tag=a&b", "eq=1=1"], Shown(query));
    }

    [Fact]
    public void OrderDuplicatesAndBareFlags_Survive()
    {
        string url = "https://x/p?id=1&id=2&debug&empty=";

        var query = UrlQuery.Parse(url);

        Assert.Equal(["id=1", "id=2", "debug", "empty="], Shown(query));
        Assert.Equal(url, query.ToString());
    }

    [Fact]
    public void AnUneditedUrl_IsWrittenBackExactlyAsTyped()
    {
        string url = "https://x/search?q=%E2%9C%93&list=1,2,3&raw=a%2Cb#frag";

        Assert.Equal(url, UrlQuery.Parse(url).ToString());
    }

    [Fact]
    public void EditingOneArgument_RewritesOnlyThatOne()
    {
        var query = UrlQuery.Parse("https://x/?raw=a%2Cb&page=2#frag");

        var edited = query.WithArgument(1, new QueryArgument("page", "3 & up"));

        Assert.Equal("https://x/?raw=a%2Cb&page=3%20%26%20up#frag", edited.ToString());
    }

    [Fact]
    public void AddingAndRemoving_KeepTheRest()
    {
        var query = UrlQuery.Parse("https://x/?a=1&b=2");

        var added = query.WithAdded(new QueryArgument("c", "x=y"));
        Assert.Equal("https://x/?a=1&b=2&c=x%3Dy", added.ToString());

        var removed = added.WithRemoved(0);
        Assert.Equal("https://x/?b=2&c=x%3Dy", removed.ToString());

        var none = removed.WithRemoved(0).WithRemoved(0);
        Assert.Equal("https://x/", none.ToString());
    }

    [Fact]
    public void AddingToAUrlWithoutAQuery_StartsOne_BeforeTheFragment()
    {
        var query = UrlQuery.Parse("https://x/path#section");

        Assert.Equal("https://x/path?k=v#section", query.WithAdded(new QueryArgument("k", "v")).ToString());
    }

    [Fact]
    public void FormattingEncodesOnlyWhatWouldBreakTheQuery()
    {
        var query = UrlQuery.Parse("https://x/").WithAdded(new QueryArgument("when", "2024-01-01T10:00:00Z,/path@x+1 ✓"));

        Assert.Equal("https://x/?when=2024-01-01T10:00:00Z,/path@x%2B1%20%E2%9C%93", query.ToString());
    }

    [Fact]
    public void EmptySegmentsAndAQuestionMarkWithNothing_AreNotArguments()
    {
        Assert.Empty(UrlQuery.Parse("https://x/?").Arguments);
        Assert.Equal(["a=1", "b=2"], Shown(UrlQuery.Parse("https://x/?a=1&&b=2&")));
    }

    [Fact]
    public void TextThatIsNotAUrlYet_StillParses()
    {
        var query = UrlQuery.Parse("api.example.com/x?y=1");

        Assert.Equal("api.example.com/x", query.Base);
        Assert.Equal(["y=1"], Shown(query));
    }

    [Fact]
    public void MalformedEscapes_AreShownAsTyped()
    {
        Assert.Equal(["p=100%", "q=%zz"], Shown(UrlQuery.Parse("https://x/?p=100%&q=%zz")));
    }
}
