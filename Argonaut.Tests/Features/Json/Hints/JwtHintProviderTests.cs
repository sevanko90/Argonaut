using System.Buffers.Text;
using System.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Hints;

public class JwtHintProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    private static string Token(string header, string claims) => $"{Part(header)}.{Part(claims)}.c2lnbmF0dXJl";

    private static string? Hint(string value)
    {
        var provider = new JwtHintProvider(new FixedClock());
        byte[] raw = Encoding.UTF8.GetBytes(value);
        return provider.TryClassify(JsonTokenKind.String, raw, raw.Length, out var candidate) ? provider.FormatHint(candidate, raw, raw.Length, 0)?.Text : null;
    }

    [Fact]
    public void AToken_ShowsItsAlgorithmAndWhenItExpires()
    {
        long inAnHour = Now.AddHours(1).ToUnixTimeSeconds();
        Assert.Equal("JWT HS256 · expires in 1 hour", Hint(Token("""{"alg":"HS256","typ":"JWT"}""", $$"""{"sub":"u1","exp":{{inAnHour}}}""")));
    }

    [Fact]
    public void AnExpiredToken_SaysSo()
    {
        long twoDaysAgo = Now.AddDays(-2).ToUnixTimeSeconds();
        Assert.Equal("JWT RS256 · expired 2 days ago", Hint(Token("""{"typ":"JWT","alg":"RS256"}""", $$"""{"exp":{{twoDaysAgo}}}""")));
    }

    [Fact]
    public void ATokenWithNoExpiry_ShowsItsAlgorithm()
        => Assert.Equal("JWT none", Hint(Token("""{"alg":"none"}""", """{"sub":"u1"}""")));

    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ1In0")]               // two parts
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ1In0.c2ln.ZXh0cmE")] // four parts
    [InlineData("eyJ!!!.eyJ.c2ln")]                                     // not base64url
    [InlineData("abc.def.ghi")]                                         // no JSON header
    public void AnythingElse_IsNotAToken(string value) => Assert.Null(Hint(value));

    [Fact]
    public void AHeaderWithNoAlgorithm_IsNotAToken()
        => Assert.Null(Hint(Token("""{"typ":"JWT"}""", """{"sub":"u1"}""")));
}
