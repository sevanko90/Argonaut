using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The raw viewer's row decode must survive anything: invalid UTF-8 becomes U+FFFD, control
/// bytes become visible Control Picture glyphs, tabs pass through, and only real line ends
/// have their newline bytes trimmed.
/// </summary>
public class RawRowReaderTests
{
    private static string ReadRowFromBytes(byte[] content, long start, long endExclusive, bool isSoftWrapped)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var file = new MMapFile(path);
            return RawRowReader.ReadRow(file, start, endExclusive, isSoftWrapped);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlainText_RoundTrips()
    {
        Assert.Equal("hello world", ReadRowFromBytes("hello world"u8.ToArray(), 0, 11, false));
    }

    [Fact]
    public void RealLineEnd_TrimsTrailingNewlineAndCarriageReturn()
    {
        Assert.Equal("abc", ReadRowFromBytes("abc\n"u8.ToArray(), 0, 4, false));
        Assert.Equal("abc", ReadRowFromBytes("abc\r\n"u8.ToArray(), 0, 5, false));
    }

    [Fact]
    public void SoftWrappedRow_IsNeverTrimmed()
    {
        // A soft-wrapped segment can't end in '\n' (peek rule), but a '\r' at the break is
        // data as far as this row is concerned - it must surface (as a control picture), not
        // silently vanish.
        Assert.Equal("ab␍", ReadRowFromBytes("ab\rx"u8.ToArray(), 0, 3, true));
    }

    [Fact]
    public void InvalidUtf8_DecodesToReplacementCharacter()
    {
        Assert.Equal("a�b", ReadRowFromBytes([(byte)'a', 0xFF, (byte)'b'], 0, 3, false));
    }

    [Fact]
    public void ControlBytes_BecomeControlPictures()
    {
        Assert.Equal("␀A␁␛", ReadRowFromBytes([0x00, (byte)'A', 0x01, 0x1B], 0, 4, false));
        Assert.Equal("␡", ReadRowFromBytes([0x7F], 0, 1, false));
    }

    [Fact]
    public void Tab_PassesThrough()
    {
        Assert.Equal("a\tb", ReadRowFromBytes("a\tb"u8.ToArray(), 0, 3, false));
    }

    [Fact]
    public void MultibyteText_RoundTrips()
    {
        byte[] content = Encoding.UTF8.GetBytes("héllo 日本語 🚀\n");
        Assert.Equal("héllo 日本語 🚀", ReadRowFromBytes(content, 0, content.Length, false));
    }

    [Fact]
    public void MidFileRange_ReadsOnlyThatRange()
    {
        byte[] content = "aaaa\nbbbb\ncccc\n"u8.ToArray();
        Assert.Equal("bbbb", ReadRowFromBytes(content, 5, 10, false));
    }
}
