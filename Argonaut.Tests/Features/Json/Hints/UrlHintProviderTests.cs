using System.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

namespace Argonaut.Tests.Features.Json.Hints;

public class UrlHintProviderTests
{
    private static ValueHint? Hint(string value)
    {
        var provider = new UrlHintProvider();
        byte[] raw = Encoding.UTF8.GetBytes(value);
        return provider.TryClassify(JsonTokenKind.String, raw, raw.Length, out var candidate) ? provider.FormatHint(candidate, raw, raw.Length, 0) : null;
    }

    [Fact]
    public void AWebAddress_OffersToOpenItsHost_AsAnAction()
    {
        var hint = Hint("https://shop.example.co.uk/basket?item=1&ref=email#pay")!;
        Assert.Equal("Open shop.example.co.uk", hint.Text);
        Assert.Equal(TreeRunStyle.Action, hint.Style);
        Assert.Equal(new Uri("https://shop.example.co.uk/basket?item=1&ref=email#pay"), Assert.IsType<OpenUrlLink>(hint.Link).Address);
        Assert.Same(hint.Link, hint.ValueLink);
    }

    [Fact]
    public void AMailAddress_OffersToEmailIt()
        => Assert.Equal("Email support@example.com", Hint("mailto:support@example.com?subject=Order%2088001")?.Text);

    /// <summary>The host is the URI parser's, not the first name in the text.</summary>
    [Fact]
    public void AMisleadingAddress_NamesTheHostItReallyGoesTo()
        => Assert.Equal("Open evil.example.net", Hint("https://good.example.com@evil.example.net/login")?.Text);

    /// <summary>A look-alike letter from another script shows as the punycode it is.</summary>
    [Fact]
    public void AnInternationalHost_IsNamedInAscii()
        => Assert.Equal("Open xn--pple-43d.com", Hint("https://аpple.com/")?.Text);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("see https://example.com/help for details")]
    [InlineData("https://")]
    [InlineData("https:\\/\\/example.com")]
    [InlineData("https://exa mple.com")]
    public void AnythingElse_GetsNothing(string value) => Assert.Null(Hint(value));

    [Fact]
    public void OnlyASafeSchemeMayBeOpened()
    {
        Assert.True(UrlHintProvider.IsSafe(new Uri("HTTPS://example.com")));
        Assert.True(UrlHintProvider.IsSafe(new Uri("mailto:a@example.com")));
        Assert.False(UrlHintProvider.IsSafe(new Uri("javascript:alert(1)")));
        Assert.False(UrlHintProvider.IsSafe(new Uri("file:///etc/passwd")));
    }
}
