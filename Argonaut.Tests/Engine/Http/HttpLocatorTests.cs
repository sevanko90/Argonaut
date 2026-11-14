using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Http;

/// <summary>A request as a place a document comes from: how it is named, shown, and told apart.</summary>
public sealed class HttpLocatorTests
{
    private static HttpLocator Locator(string url, HttpRequestMethod method = HttpRequestMethod.Get, string? body = null)
    {
        HttpRequestSpec.TryCreate(method, url, body is null ? null : Encoding.UTF8.GetBytes(body), out var request, out var problem);
        return new HttpLocator(request ?? throw new InvalidOperationException(problem));
    }

    [Fact]
    public void IsNamedForItsLastPathSegmentAndHost()
    {
        Assert.Equal("items · api.example.com", Locator("https://api.example.com/v1/items?page=2").DefaultName);
        Assert.Equal("api.example.com", Locator("https://api.example.com/").DefaultName);
    }

    [Fact]
    public void IsShownWithItsMethod()
    {
        Assert.Equal("POST https://x.test/a?b=1", Locator("https://x.test/a?b=1", HttpRequestMethod.Post, "{}").DisplayText);
    }

    [Fact]
    public void TheSameRequestSpelledDifferently_IsTheSamePlace()
    {
        Assert.True(Locator("HTTPS://API.Example.com/items?page=2#top").IsSamePlace(Locator("https://api.example.com/items?page=2")));
    }

    [Fact]
    public void PathCaseQueryAndMethod_MakeDifferentPlaces()
    {
        var get = Locator("https://x.test/items?page=2");

        Assert.False(get.IsSamePlace(Locator("https://x.test/Items?page=2")));
        Assert.False(get.IsSamePlace(Locator("https://x.test/items?page=3")));
        Assert.False(get.IsSamePlace(Locator("https://x.test/items?page=2", HttpRequestMethod.Post, "")));
    }

    [Fact]
    public void PostsWithDifferentBodies_AreDifferentPlaces()
    {
        Assert.False(Locator("https://x.test/q", HttpRequestMethod.Post, "{\"a\":1}")
            .IsSamePlace(Locator("https://x.test/q", HttpRequestMethod.Post, "{\"a\":2}")));
        Assert.True(Locator("https://x.test/q", HttpRequestMethod.Post, "{\"a\":1}")
            .IsSamePlace(Locator("https://x.test/q", HttpRequestMethod.Post, "{\"a\":1}")));
    }

    [Fact]
    public void ARequestIsNeverTheSamePlaceAsAFile()
    {
        Assert.False(Locator("https://x.test/a.json").IsSamePlace(new FileLocator(Path.Combine(Path.GetTempPath(), "a.json"))));
    }
}
