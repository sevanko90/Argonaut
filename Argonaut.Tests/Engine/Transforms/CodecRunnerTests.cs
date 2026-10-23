using System.Buffers;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Transforms;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Transforms;

public class CodecRunnerTests
{
    private static string Run((TextCodec Codec, CodecScope Scope) codec, string input,
        TransformDirection direction = TransformDirection.Forward, bool inputIsPrefix = false) =>
        Encoding.UTF8.GetString(RunBytes(codec, new MemoryByteSource(Encoding.UTF8.GetBytes(input)), direction, inputIsPrefix));

    private static byte[] RunBytes((TextCodec Codec, CodecScope Scope) codec, IByteSource input,
        TransformDirection direction = TransformDirection.Forward, bool inputIsPrefix = false)
    {
        var output = new ArrayBufferWriter<byte>();
        CodecRunner.Run(codec.Codec, direction, codec.Scope, input, output, CancellationToken.None, inputIsPrefix);
        return output.WrittenSpan.ToArray();
    }

    [Theory]
    [InlineData(TextCodec.JsonString, "line 1\n\"quoted\"\t\u0001 é", "line 1\\n\\\"quoted\\\"\\t\\u0001 é")]
    [InlineData(TextCodec.Url, "a b/c?d=é", "a%20b%2Fc%3Fd%3D%C3%A9")]
    [InlineData(TextCodec.Base64, "hello?>", "aGVsbG8/Pg==")]
    [InlineData(TextCodec.Base64Url, "hello?>", "aGVsbG8_Pg")]
    public void WholeText_EncodesAndDecodes(TextCodec which, string text, string encoded)
    {
        var codec = (which, CodecScope.WholeText);

        Assert.Equal(encoded, Run(codec, text));
        Assert.Equal(text, Run(codec, encoded, TransformDirection.Reverse));
    }

    [Theory]
    [InlineData(TextCodec.Base64)]
    [InlineData(TextCodec.Base64Url)]
    public void Base64_EachLine_EncodesLinesSeparately_KeepingLineBreaks(TextCodec which)
    {
        var codec = (which, CodecScope.EachLine);

        string encoded = Run(codec, "a\nbb\n");

        Assert.Equal(which == TextCodec.Base64 ? "YQ==\nYmI=\n" : "YQ\nYmI\n", encoded);
        Assert.Equal("a\nbb\n", Run(codec, encoded, TransformDirection.Reverse));
    }

    [Fact]
    public void Base64Decode_IgnoresLineWrapping_AndToleratesMissingPadding()
    {
        var codec = (TextCodec.Base64, CodecScope.WholeText);

        Assert.Equal("hello world", Run(codec, "aGVsbG8g\r\nd29y\nbGQ", TransformDirection.Reverse));
    }

    [Fact]
    public void Base64Decode_RefusesText_ThatIsNotBase64()
    {
        Assert.Throws<TransformException>(() =>
            Run((TextCodec.Base64, CodecScope.WholeText), "not*base64", TransformDirection.Reverse));
    }

    [Fact]
    public void Base64Decode_OfAPrefix_StopsAtTheLastWholeQuantum()
    {
        var codec = (TextCodec.Base64, CodecScope.WholeText);

        Assert.Equal("hello", Run(codec, "aGVsbG8gd2", TransformDirection.Reverse, inputIsPrefix: true)[..5]);
    }

    [Fact]
    public void JsonStringDecode_AcceptsTheSurroundingQuotes_AndSurrogatePairs()
    {
        var codec = (TextCodec.JsonString, CodecScope.EachLine);

        Assert.Equal("a\"😀", Run(codec, "\"a\\\"\\ud83d\\ude00\"", TransformDirection.Reverse));
    }

    [Fact]
    public void JsonStringDecode_ReportsAnUnknownEscape_WithItsLine()
    {
        var thrown = Assert.Throws<TransformException>(() =>
            Run((TextCodec.JsonString, CodecScope.EachLine), "ok\nbad \\q", TransformDirection.Reverse));

        Assert.StartsWith("Line 2:", thrown.Message);
    }

    [Fact]
    public void UrlDecode_RefusesABrokenPercentSequence()
    {
        Assert.Throws<TransformException>(() =>
            Run((TextCodec.Url, CodecScope.EachLine), "100%", TransformDirection.Reverse));
    }

    [Fact]
    public void Base64_OfMoreThanOneCodecChunk_RoundTrips()
    {
        var input = new byte[200_001];
        new Random(1).NextBytes(input);
        var codec = (TextCodec.Base64, CodecScope.WholeText);

        byte[] encoded = RunBytes(codec, new MemoryByteSource(input));

        Assert.Equal(Convert.ToBase64String(input), Encoding.ASCII.GetString(encoded));
        Assert.Equal(input, RunBytes(codec, new MemoryByteSource(encoded), TransformDirection.Reverse));
    }

    /// <summary>A line, an escape or a base64 quantum straddling a run boundary must read the
    /// same as in one run.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void SplitSource_GivesTheSameResultAsOneRun(int pieceSize)
    {
        var text = new StringBuilder();
        for (int i = 0; i < 200; i++)
            text.Append("line ").Append(i).Append(" 'q' \"d\" \\ é\n");

        byte[] input = Encoding.UTF8.GetBytes(text.ToString());
        foreach (var codec in Enum.GetValues<TextCodec>())
        {
            foreach (var scope in Enum.GetValues<CodecScope>())
            {
                byte[] whole = RunBytes((codec, scope), new MemoryByteSource(input));
                Assert.Equal(whole, RunBytes((codec, scope), new SplitByteSource(input, pieceSize)));
                Assert.Equal(input, RunBytes((codec, scope), new SplitByteSource(whole, pieceSize), TransformDirection.Reverse));
            }
        }
    }

    [Fact]
    public void Run_StopsWhenAsked()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var input = new MemoryByteSource(Encoding.UTF8.GetBytes(new string('a', 100_000)));

        Assert.ThrowsAny<OperationCanceledException>(() => CodecRunner.Run(
            TextCodec.Base64, TransformDirection.Forward, CodecScope.WholeText, input,
            new ArrayBufferWriter<byte>(), stopping.Token));
    }
}
