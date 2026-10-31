using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Detection;
using Argonaut.Engine.Transforms;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Transforms;

public class LineEndingConverterTests
{
    [Theory]
    [InlineData("a\nb\nc", LineEndingStyle.CrLf, "a\r\nb\r\nc")]
    [InlineData("a\r\nb\r\nc\r\n", LineEndingStyle.Lf, "a\nb\nc\n")]
    [InlineData("a\rb\rc", LineEndingStyle.Lf, "a\nb\nc")]
    [InlineData("a\rb\r", LineEndingStyle.CrLf, "a\r\nb\r\n")]
    [InlineData("a\nb\r\nc\rd", LineEndingStyle.Lf, "a\nb\nc\nd")]
    [InlineData("a\nb\r\nc\rd", LineEndingStyle.CrLf, "a\r\nb\r\nc\r\nd")]
    [InlineData("\r\n\r\n", LineEndingStyle.Lf, "\n\n")]
    [InlineData("\n\n", LineEndingStyle.CrLf, "\r\n\r\n")]
    [InlineData("no breaks", LineEndingStyle.CrLf, "no breaks")]
    [InlineData("", LineEndingStyle.Lf, "")]
    public void EveryBreak_IsWrittenAsTheTarget(string text, LineEndingStyle target, string expected)
    {
        foreach (int pieceSize in new[] { 1, 2, 3, 4096 })
        {
            var (written, _) = Convert(text, target, pieceSize);
            Assert.Equal(expected, written);
        }
    }

    [Theory]
    [InlineData("a\nb\r\nc\rd\r", LineEndingStyle.Lf)]
    [InlineData("a\nb\r\nc\rd\r", LineEndingStyle.CrLf)]
    [InlineData("\r\r\n\n\r", LineEndingStyle.CrLf)]
    [InlineData("\r\r\n\n\r", LineEndingStyle.Lf)]
    public void TheMeasuredLength_IsWhatIsWritten(string text, LineEndingStyle target)
    {
        var source = new SplitByteSource(Encoding.ASCII.GetBytes(text), 2);

        long measured = new LineEndingConverter().MeasureOutput(source, target, CancellationToken.None);
        var (written, _) = Convert(text, target, pieceSize: 2);

        Assert.Equal(written.Length, measured);
    }

    [Theory]
    [InlineData("ab\r\ncd", 4, LineEndingStyle.Lf, 3)]     // 'c' follows the shortened break
    [InlineData("ab\ncd", 3, LineEndingStyle.CrLf, 4)]     // 'c' follows the lengthened break
    [InlineData("ab\r\ncd", 2, LineEndingStyle.Lf, 2)]     // on the break: its start
    [InlineData("ab\r\ncd", 3, LineEndingStyle.Lf, 2)]     // between CR and LF: the break's start
    [InlineData("ab\ncd", 5, LineEndingStyle.CrLf, 6)]     // the end stays the end
    [InlineData("a\nb\nc", 4, LineEndingStyle.CrLf, 6)]    // after two breaks
    public void ATrackedOffset_FollowsItsText(string text, long offset, LineEndingStyle target, long expected)
    {
        foreach (int pieceSize in new[] { 1, 2, 4096 })
        {
            var (_, tracked) = Convert(text, target, pieceSize, offset);
            Assert.Equal(expected, tracked);
        }
    }

    [Fact]
    public void SmallWrites_AreGatheredIntoTheBuffer()
    {
        string text = string.Concat(Enumerable.Repeat("x\n", 10_000));
        var stream = new CountingStream();

        new LineEndingConverter(outputBufferBytes: 4096).WriteConverted(
            new MemoryByteSource(Encoding.ASCII.GetBytes(text)), LineEndingStyle.CrLf, stream, null, CancellationToken.None, 0);

        Assert.Equal(30_000, stream.Length);
        Assert.InRange(stream.Writes, 1, 30_000 / 4096 + 2);
    }

    [Fact]
    public void AStoppedConversion_Throws()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new LineEndingConverter().WriteConverted(
            new MemoryByteSource("a\nb"u8.ToArray()), LineEndingStyle.CrLf, new MemoryStream(), null, stopping.Token, 0));
    }

    [Theory]
    [InlineData("a\r\nb\nc", LineEndingStyle.Lf, "a\nb\nc")]
    [InlineData("a\r\nb\nc", LineEndingStyle.CrLf, "a\r\nb\r\nc")]
    [InlineData("a\rb\nc", LineEndingStyle.CrLf, "a\rb\r\nc")]   // a lone CR is left alone
    [InlineData("\n", LineEndingStyle.CrLf, "\r\n")]
    [InlineData("plain", LineEndingStyle.CrLf, "plain")]
    public void APaste_TakesTheDocumentsBreaks(string pasted, LineEndingStyle target, string expected)
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        new LineEndingConverter().ConvertPaste(Encoding.ASCII.GetBytes(pasted), target, output);

        Assert.Equal(expected, Encoding.ASCII.GetString(output.WrittenSpan));
    }

    private static (string Written, long Tracked) Convert(string text, LineEndingStyle target, int pieceSize, long trackedOffset = 0)
    {
        var stream = new MemoryStream();
        long tracked = new LineEndingConverter(outputBufferBytes: 3).WriteConverted(
            new SplitByteSource(Encoding.ASCII.GetBytes(text), pieceSize), target, stream, null, CancellationToken.None, trackedOffset);
        return (Encoding.ASCII.GetString(stream.ToArray()), tracked);
    }

    private sealed class CountingStream : Stream
    {
        public int Writes { get; private set; }
        public override long Length => this.length;
        private long length;

        public override void Write(byte[] buffer, int offset, int count) { Writes++; this.length += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { Writes++; this.length += buffer.Length; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => this.length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
