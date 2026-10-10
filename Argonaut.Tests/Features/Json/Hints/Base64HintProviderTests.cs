using System.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Hints;

public class Base64HintProviderTests
{
    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 32, 0, 0, 0, 16, 8, 6, 0, 0, 0];

    private static string? Hint(string value, long? fullLength = null, int? givenLength = null)
    {
        var provider = new Base64HintProvider();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        if (givenLength is { } given)
            bytes = bytes[..given];
        long length = fullLength ?? value.Length;
        return provider.TryClassify(JsonTokenKind.String, bytes, length, out var candidate)
            ? provider.FormatHint(candidate, bytes, length, 0)?.Text
            : null;
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes);

    [Fact]
    public void APng_SaysItsSizeInPixelsAndBytes()
        => Assert.Equal("PNG 32×16 · 129 bytes", Hint(Encode([.. PngHeader, .. new byte[100]])));

    [Fact]
    public void Gzip_IsNamed()
        => Assert.Equal("gzip · 50 bytes", Hint(Encode([0x1F, 0x8B, 0x08, 0, .. new byte[46]])));

    [Fact]
    public void Text_IsPreviewed()
    {
        Assert.Equal("Base64 text · “Hello world!” · 12 bytes", Hint(Encode(Encoding.UTF8.GetBytes("Hello world!"))));
        Assert.Equal("Base64 text · “The quick brown fox…” · 44 bytes",
            Hint(Encode(Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog."))));
    }

    [Fact]
    public void UrlSafeBase64_IsReadToo()
        => Assert.StartsWith("Base64 text", Hint(Convert.ToBase64String(Encoding.UTF8.GetBytes("subjects?>>and~~more"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=')));

    /// <summary>Only the start of a long value is read; its size comes from its length.</summary>
    [Fact]
    public void ALongValue_IsReadFromItsStart()
    {
        string encoded = Encode([.. PngHeader, .. new byte[3 * 1024 * 1024]]);
        Assert.Equal("PNG 32×16 · 3 MB", Hint(encoded, fullLength: encoded.Length, givenLength: 1024));
    }

    [Theory]
    [InlineData("Administrator123")]                                            // alphabet, but decodes to noise
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")] // a hex hash
    [InlineData("Zm9v!YmFy")]
    [InlineData("SGVsbG8=")]                                                    // too short to be sure
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")]                // binary with no signature
    [InlineData("SGVsbG8gd29ybGQh=A")]                                          // padding inside
    public void AnythingElse_GetsNothing(string value) => Assert.Null(Hint(value));
}
