using System.Text;
using Argonaut.Features.Search;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the literal byte matcher: ASCII case folding (and its deliberate absence for
/// non-ASCII bytes), the 'from' offset contract, matches ending exactly at the window end,
/// and that partial occurrences at the end of a window are never reported.
/// </summary>
public class LiteralSearchMatcherTests
{
    private static bool Find(LiteralSearchMatcher matcher, string haystack, int from, out int index, out int length)
        => matcher.TryFindNext(Encoding.UTF8.GetBytes(haystack), from, out index, out length);

    [Fact]
    public void FindsMatch_CaseInsensitiveByDefault()
    {
        var matcher = new LiteralSearchMatcher("hello");

        Assert.True(Find(matcher, "xxHELLOxx", 0, out int index, out int length));
        Assert.Equal(2, index);
        Assert.Equal(5, length);
    }

    [Fact]
    public void CaseSensitive_RequiresExactBytes()
    {
        var matcher = new LiteralSearchMatcher("hello", ignoreCase: false);

        Assert.False(Find(matcher, "xxHELLOxx", 0, out _, out _));
        Assert.True(Find(matcher, "xxhelloxx", 0, out int index, out _));
        Assert.Equal(2, index);
    }

    [Fact]
    public void NonAsciiBytes_NeverCaseFolded()
    {
        // é vs É differ in their UTF-8 bytes; ASCII-only folding must not equate them.
        var matcher = new LiteralSearchMatcher("café");

        Assert.False(Find(matcher, "CAFÉ", 0, out _, out _));
        Assert.True(Find(matcher, "CAFé", 0, out int index, out _));
        Assert.Equal(0, index);
    }

    [Fact]
    public void FromOffset_SkipsEarlierMatches()
    {
        var matcher = new LiteralSearchMatcher("ab");

        Assert.True(Find(matcher, "abab", 1, out int index, out _));
        Assert.Equal(2, index);
    }

    [Fact]
    public void MatchEndingAtWindowEnd_IsFound()
    {
        var matcher = new LiteralSearchMatcher("ab");

        Assert.True(Find(matcher, "xxab", 0, out int index, out _));
        Assert.Equal(2, index);
    }

    [Fact]
    public void PartialMatchAtWindowEnd_IsNotReported()
    {
        var matcher = new LiteralSearchMatcher("ab");

        Assert.False(Find(matcher, "xxa", 0, out _, out _));
    }

    [Fact]
    public void FromBeyondPossibleStart_ReturnsFalse()
    {
        var matcher = new LiteralSearchMatcher("ab");

        Assert.False(Find(matcher, "ab", 1, out _, out _));
    }

    [Fact]
    public void EmptyTerm_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LiteralSearchMatcher(""));
    }

    [Fact]
    public void WindowOverlap_IsNeedleLengthMinusOne()
    {
        Assert.Equal(4, new LiteralSearchMatcher("hello").WindowOverlap);
        Assert.Equal(0, new LiteralSearchMatcher("h").WindowOverlap);
    }
}
