using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;

namespace Argonaut.Tests.Engine.Bytes;

/// <summary>
/// An excerpt is the whole document's bytes, offset: everything opened over it reads only its
/// lines, and it never claims to be the file it came from.
/// </summary>
public class ExcerptByteOriginTests
{
    private const string Text = "one\ntwo\nthree\nfour\n";

    private static ExcerptByteOrigin Excerpt(long first, long? last)
    {
        var whole = new MemoryByteOrigin(Encoding.UTF8.GetBytes(Text), "big.ndjson");
        var source = whole.Open();
        try
        {
            return new ExcerptByteOrigin(whole, LineRangeLocator.Locate(source, first, last)!.Value);
        }
        finally
        {
            source.Release();
        }
    }

    private static string Read(IByteSource source)
    {
        try
        {
            return source.GetUtf8String(0, (int)source.AvailableLength);
        }
        finally
        {
            source.Release();
        }
    }

    [Fact]
    public void Open_reads_only_the_excerpt()
    {
        using var excerpt = Excerpt(2, 3);

        Assert.Equal(10, excerpt.AvailableLength);
        Assert.Equal("two\nthree\n", Read(excerpt.Open()));
    }

    [Fact]
    public void OpenRange_is_relative_to_the_excerpt()
    {
        using var excerpt = Excerpt(2, 3);

        Assert.Equal("three", Read(excerpt.OpenRange(4, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => excerpt.OpenRange(4, 7));
    }

    [Fact]
    public void Has_no_path_and_names_its_lines()
    {
        using var excerpt = Excerpt(2, 3);

        Assert.Null(excerpt.Path);
        Assert.Equal("big.ndjson (lines 2–3)", excerpt.DisplayName);
        Assert.Equal("big.ndjson (line 4)", Excerpt(4, 4).DisplayName);
        Assert.Equal("big.ndjson (lines 2–end)", Excerpt(2, null).DisplayName);
    }

    [Fact]
    public void A_byte_excerpt_reads_its_range_and_names_it_by_bytes()
    {
        var whole = new MemoryByteOrigin(Encoding.UTF8.GetBytes(Text), "big.json");
        using var excerpt = ExcerptByteOrigin.OfBytes(whole, new ByteRange(4, 3));

        Assert.Equal("two", Read(excerpt.Open()));
        Assert.Null(excerpt.Lines);
        Assert.Equal("big.json (bytes 4–6)", excerpt.DisplayName);
    }

    [Fact]
    public void Its_short_description_rounds_for_a_label()
    {
        var whole = new MemoryByteOrigin(new byte[3_000_000], "big.bin");
        using var excerpt = ExcerptByteOrigin.OfBytes(whole, new ByteRange(1_500_000, 1_000_000));

        Assert.Equal("bytes 1.5m–2.5m", excerpt.ShortDescription);
    }

    [Fact]
    public void Its_short_description_keeps_close_ends_apart()
    {
        var whole = new MemoryByteOrigin(new byte[1_100_000], "big.bin");
        using var bytes = ExcerptByteOrigin.OfBytes(whole, new ByteRange(1_000_000, 201));
        using var lines = new ExcerptByteOrigin(whole, new LocatedLines(new ByteRange(0, 10), 1_000_000, 1_000_300));

        Assert.Equal("bytes 1m–1.0002m", bytes.ShortDescription);
        Assert.Equal("lines 1m–1.0003m", lines.ShortDescription);
    }
}
