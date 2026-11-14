using System.Text;
using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Http;

/// <summary>What can be sent: an absolute http or https URL, and a body only for POST.</summary>
public sealed class HttpRequestSpecTests
{
    [Theory]
    [InlineData("https://api.example.com/items?page=2")]
    [InlineData("http://localhost:8080/")]
    public void AnAbsoluteHttpUrl_MakesARequest(string url)
    {
        Assert.True(HttpRequestSpec.TryCreate(HttpRequestMethod.Get, url, body: null, out var request, out var problem));
        Assert.Null(problem);
        Assert.Equal(new Uri(url), request!.Url);
    }

    [Theory]
    [InlineData("", "Enter a URL")]
    [InlineData("api.example.com/items", "http:// or https://")]
    [InlineData("ftp://example.com/file", "http:// or https://")]
    [InlineData("https://", "not a valid URL")]
    public void AnythingElse_SaysWhy(string url, string expected)
    {
        Assert.False(HttpRequestSpec.TryCreate(HttpRequestMethod.Get, url, body: null, out var request, out var problem));
        Assert.Null(request);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void AGet_CarriesNoBody_EvenIfOneWasTyped()
    {
        HttpRequestSpec.TryCreate(HttpRequestMethod.Get, "https://x/", Encoding.UTF8.GetBytes("{}"), out var request, out _);

        Assert.Null(request!.Body);
    }

    [Fact]
    public void APost_CarriesItsBody()
    {
        HttpRequestSpec.TryCreate(HttpRequestMethod.Post, "https://x/", Encoding.UTF8.GetBytes("{\"a\":1}"), out var request, out _);

        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(request!.Body!));
    }

    [Fact]
    public void SurroundingWhitespace_IsIgnored()
    {
        Assert.True(HttpRequestSpec.TryCreate(HttpRequestMethod.Get, "  https://x/a \n", null, out var request, out _));
        Assert.Equal("https://x/a", request!.Url.AbsoluteUri);
    }
}
