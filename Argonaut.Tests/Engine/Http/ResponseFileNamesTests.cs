using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Http;

/// <summary>The name a saved copy of a response is offered under: the request's name, safe for a
/// file system, with the extension its media type implies.</summary>
public sealed class ResponseFileNamesTests
{
    [Theory]
    [InlineData("application/json", ".json")]
    [InlineData("APPLICATION/JSON", ".json")]
    [InlineData("application/geo+json", ".json")]
    [InlineData("application/problem+json", ".json")]
    [InlineData("application/x-ndjson", ".ndjson")]
    [InlineData("text/csv", ".csv")]
    [InlineData("text/tab-separated-values", ".tsv")]
    [InlineData("application/xml", ".xml")]
    [InlineData("application/atom+xml", ".xml")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("text/html", ".html")]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/octet-stream", ".bin")]
    [InlineData("text/x-unheard-of", ".txt")]
    [InlineData("application/x-unheard-of", ".bin")]
    [InlineData(null, ".txt")]
    [InlineData("", ".txt")]
    public void TheExtension_FollowsTheMediaType(string? mediaType, string expected)
        => Assert.Equal(expected, ResponseFileNames.ExtensionFor(mediaType));

    [Fact]
    public void TheRequestsName_GetsTheExtension()
        => Assert.Equal("items · api.example.com.json", ResponseFileNames.Suggest("items · api.example.com", "application/json"));

    [Fact]
    public void ANameAlreadyEndingInTheExtension_IsNotGivenItTwice()
        => Assert.Equal("logo.png", ResponseFileNames.Suggest("logo.png", "image/png"));

    [Theory]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j.json")]
    [InlineData("   ", "response.json")]
    [InlineData("..", "response.json")]
    public void CharactersNoFileSystemTakes_AreReplaced(string name, string expected)
        => Assert.Equal(expected, ResponseFileNames.Suggest(name, "application/json"));
}
