using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Http;

namespace Argonaut.Tests.Engine.Detection;

/// <summary>
/// Telling binary from text: a decisive media type wins, an undecided one leaves it to the first
/// bytes - a known signature, or a NUL outside UTF-16/32 - and detection routes binary documents
/// to the hex view before any text rule runs.
/// </summary>
public sealed class BinaryContentTests : IDisposable
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private readonly string folder = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    public BinaryContentTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/JPEG", true)]
    [InlineData("application/pdf", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("application/json", false)]
    [InlineData("application/hal+json", false)]
    [InlineData("text/csv", false)]
    [InlineData("application/octet-stream", null)]
    [InlineData("application/x-something", null)]
    [InlineData(null, null)]
    public void AMediaType_DecidesOnlyWhenItIsDecisive(string? mediaType, bool? expected)
        => Assert.Equal(expected, BinaryContent.IsBinaryMediaType(mediaType));

    [Fact]
    public void ASignature_NamesTheFormat()
    {
        Assert.Equal("PNG image", BinaryContent.FormatOf(Png));
        Assert.Equal("JPEG image", BinaryContent.FormatOf([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("GIF image", BinaryContent.FormatOf("GIF89a..."u8));
        Assert.Equal("PDF document", BinaryContent.FormatOf("%PDF-1.7\n"u8));
        Assert.Equal("ZIP archive", BinaryContent.FormatOf([(byte)'P', (byte)'K', 3, 4, 20]));
        Assert.Null(BinaryContent.FormatOf("""{"a":1}"""u8));
    }

    [Fact]
    public void ANul_IsBinary_ButNotInUtf16Text()
    {
        Assert.True(BinaryContent.LooksBinary("abc\0def"u8));
        Assert.False(BinaryContent.LooksBinary([0xFF, 0xFE, (byte)'a', 0, (byte)'b', 0]));
        Assert.False(BinaryContent.LooksBinary("plain text, nothing odd\n"u8));
        Assert.False(BinaryContent.LooksBinary([]));
    }

    [Fact]
    public void ANulBeyondTheSniff_IsNotSeen()
    {
        var text = Encoding.ASCII.GetBytes(new string('x', BinaryContent.SniffLength) + "\0");
        Assert.False(BinaryContent.LooksBinary(text));
    }

    [Fact]
    public void Detection_RoutesSniffedBinaryToTheHexView()
    {
        using var origin = new MemoryByteOrigin(Png, "logo");
        Assert.Equal(FileTypeDetector.FileKind.Hex, FileTypeDetector.DetectFileType(origin));
    }

    [Fact]
    public void Detection_LeavesTextAlone()
    {
        using var origin = new MemoryByteOrigin("""{"a":1}"""u8.ToArray(), "pasted");
        Assert.Equal(FileTypeDetector.FileKind.Json, FileTypeDetector.DetectFileType(origin));
    }

    [Fact]
    public void Detection_TrustsABinaryMediaType_OverTextLookingBytes()
    {
        using var origin = Response("""{"a":1}"""u8.ToArray(), "image/png");
        Assert.Equal(FileTypeDetector.FileKind.Hex, FileTypeDetector.DetectFileType(origin));
    }

    [Fact]
    public void Detection_TrustsATextMediaType_OverAStrayNul()
    {
        using var origin = Response(Encoding.UTF8.GetBytes("{\"a\":\"x\0\"}"), "application/json");
        Assert.NotEqual(FileTypeDetector.FileKind.Hex, FileTypeDetector.DetectFileType(origin));
    }

    [Fact]
    public void Detection_SniffsWhenTheMediaTypeIsOctetStream()
    {
        using var json = Response("""{"a":1}"""u8.ToArray(), "application/octet-stream");
        using var png = Response(Png, "application/octet-stream");

        Assert.Equal(FileTypeDetector.FileKind.Json, FileTypeDetector.DetectFileType(json));
        Assert.Equal(FileTypeDetector.FileKind.Hex, FileTypeDetector.DetectFileType(png));
    }

    [Fact]
    public void ForcingTheHexView_IsAlwaysPlausible()
    {
        using var origin = new MemoryByteOrigin("""{"a":1}"""u8.ToArray(), "pasted");
        Assert.True(FileTypeDetector.IsPlausibleFor(FileTypeDetector.FileKind.Hex, origin, out _));
    }

    private HttpResponseOrigin Response(byte[] body, string mediaType)
    {
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".response");
        File.WriteAllBytes(path, body);
        HttpRequestSpec.TryCreate(HttpRequestMethod.Get, "https://x.test/a", null, out var request, out _);
        return new HttpResponseOrigin(new HttpLocator(request!), path, mediaType);
    }
}
