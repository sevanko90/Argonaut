using System.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Hints;

public class EmbeddedJsonHintProviderTests
{
    /// <summary>The hint for a string whose raw content - as it sits in the file, escapes and
    /// all - is <paramref name="raw"/>, of which only the first bytes may be given.</summary>
    private static string? Hint(string raw, long? fullLength = null)
    {
        var provider = new EmbeddedJsonHintProvider();
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        long length = fullLength ?? bytes.Length;
        return provider.TryClassify(JsonTokenKind.String, bytes, length, out var candidate)
            ? provider.FormatHint(candidate, bytes, length, 0)?.Text
            : null;
    }

    [Theory]
    [InlineData("""{\"event\":\"order.created\",\"lines\":[{\"qty\":1}],\"paid\":true}""", "JSON object · 3 members")]
    [InlineData("""{\"a\":1}""", "JSON object · 1 member")]
    [InlineData("{}", "JSON object · 0 members")]
    [InlineData("[1,2,3]", "JSON array · 3 items")]
    [InlineData("""[{\"a\":[1,2]},{\"b\":2}]""", "JSON array · 2 items")]
    [InlineData("""{\n  \"a\": 1\n}""", "JSON object · 1 member")]
    public void AStringHoldingJson_SaysWhatItHolds(string raw, string expected) => Assert.Equal(expected, Hint(raw));

    [Theory]
    [InlineData("{ looks like json, but is not }")]
    [InlineData("""{\"a\":""")]
    [InlineData("""{\"a\":1} trailing""")]
    [InlineData("[see note]")]
    [InlineData("plain text")]
    [InlineData("1234")]
    public void AStringThatIsNotJson_GetsNothing(string raw) => Assert.Null(Hint(raw));

    [Fact]
    public void ALongValue_OnlyLooksLikeJson_WithItsSize()
        => Assert.Equal("looks like JSON · 4 MB", Hint("""{\"rows\":[{\"id\":1},""", fullLength: 4 * 1024 * 1024));
}
