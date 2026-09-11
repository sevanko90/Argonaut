using System.Text;
using Argonaut.Features.Raw;

namespace Argonaut.Tests;

/// <summary>
/// Extraction is what the clipboard uses, so it must keep the bytes as they are in the file -
/// newlines intact, control characters real rather than substituted for Control Pictures - and
/// must refuse rather than attempt an enormous allocation when the selection is a whole
/// multi-GB document.
/// </summary>
public class RawTextExtractorTests
{
    private const char ReplacementChar = (char)0xFFFD;

    private static ArrayByteSource Source(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ExtractsTheRequestedRange()
    {
        Assert.True(RawTextExtractor.TryExtract(Source("hello world"), 6, 11, out string text));

        Assert.Equal("world", text);
    }

    [Fact]
    public void KeepsNewlinesAndRealControlCharacters()
    {
        // A row drops its trailing newline and substitutes Control Pictures for display; copied
        // text must do neither, or pasting it into another program is silently wrong.
        var source = new ArrayByteSource([(byte)'a', (byte)'\n', 0x07, (byte)'b']);

        Assert.True(RawTextExtractor.TryExtract(source, 0, 4, out string text));

        Assert.Equal(4, text.Length);
        Assert.Equal('a', text[0]);
        Assert.Equal('\n', text[1]);
        Assert.Equal((char)0x07, text[2]);
        Assert.Equal('b', text[3]);
    }

    [Fact]
    public void InvalidBytesBecomeReplacementCharacters()
    {
        var source = new ArrayByteSource([(byte)'a', 0xC3, (byte)'b']);

        Assert.True(RawTextExtractor.TryExtract(source, 0, 3, out string text));

        Assert.Equal("a" + ReplacementChar + "b", text);
    }

    [Fact]
    public void RangesAreClampedToTheDocument()
    {
        var source = Source("abc");

        Assert.True(RawTextExtractor.TryExtract(source, -5, 99, out string text));
        Assert.Equal("abc", text);

        Assert.True(RawTextExtractor.TryExtract(source, 2, 1, out string reversed));
        Assert.Equal(string.Empty, reversed);
    }

    [Fact]
    public void AnEmptyRangeExtractsAnEmptyString()
    {
        Assert.True(RawTextExtractor.TryExtract(Source("abc"), 1, 1, out string text));

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void ExtractionAcrossPieceBoundariesIsStitched()
    {
        var table = new RawPieceTable(Source("hello world"));
        table.Insert(5, Encoding.UTF8.GetBytes(" big"));

        Assert.True(RawTextExtractor.TryExtract(table, 0, table.Length, out string text));

        Assert.Equal("hello big world", text);
    }

    [Fact]
    public void ARangeBeyondTheCapIsRefusedRatherThanTruncated()
    {
        // Stands in for "select all" on a document far larger than memory: the answer is no, not
        // a string that looks complete and is not.
        var huge = new OversizedSource(RawTextExtractor.MaxExtractBytes + 1);

        Assert.False(RawTextExtractor.TryExtract(huge, 0, huge.Length, out string text));
        Assert.Equal(string.Empty, text);

        // A range inside the cap over the same document still works.
        Assert.True(RawTextExtractor.TryExtract(huge, 0, 4, out string small));
        Assert.Equal(4, small.Length);
    }

    /// <summary>Claims a large length without allocating it, so the cap can be tested cheaply.</summary>
    private sealed class OversizedSource : Argonaut.Infrastructure.IByteSource
    {
        private readonly byte[] window = new byte[4096];

        public OversizedSource(long length)
        {
            Length = length;
            Array.Fill(this.window, (byte)'x');
        }

        public long Length { get; }

        public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength)
            => offset < 0 || offset >= Length || maxLength <= 0
                ? ReadOnlySpan<byte>.Empty
                : this.window.AsSpan(0, (int)Math.Min(maxLength, this.window.Length));

        public int CopyTo(long offset, Span<byte> destination)
        {
            var span = GetContiguousSpan(offset, destination.Length);
            span.CopyTo(destination);
            return span.Length;
        }
    }
}
