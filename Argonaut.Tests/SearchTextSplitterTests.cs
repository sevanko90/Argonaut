using Argonaut.Features.Search;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the display-side splitter behind row highlighting: the null fast path (no term
/// or no occurrence), full coverage of the input across segments, adjacent and
/// case-insensitive matches, and a term equal to the whole string.
/// </summary>
public class SearchTextSplitterTests
{
    private static void AssertCoversText(string text, IReadOnlyList<SearchTextSplitter.Segment> segments)
    {
        int position = 0;
        foreach (var segment in segments)
        {
            Assert.Equal(position, segment.Start);
            position += segment.Length;
        }

        Assert.Equal(text.Length, position);
    }

    [Fact]
    public void NullOrEmptyTerm_ReturnsNull()
    {
        Assert.Null(SearchTextSplitter.Split("some text", null));
        Assert.Null(SearchTextSplitter.Split("some text", ""));
    }

    [Fact]
    public void TermNotPresent_ReturnsNull()
    {
        Assert.Null(SearchTextSplitter.Split("some text", "zzz"));
        Assert.Null(SearchTextSplitter.Split("ab", "abc")); // longer than the text
    }

    [Fact]
    public void SingleMatch_SplitsIntoThreeSegments()
    {
        var segments = SearchTextSplitter.Split("say hello now", "hello");

        Assert.NotNull(segments);
        AssertCoversText("say hello now", segments);
        Assert.Equal(3, segments.Count);
        Assert.False(segments[0].IsMatch);
        Assert.True(segments[1].IsMatch);
        Assert.Equal(4, segments[1].Start);
        Assert.Equal(5, segments[1].Length);
        Assert.False(segments[2].IsMatch);
    }

    [Fact]
    public void MultipleAndAdjacentMatches_AllReported()
    {
        var segments = SearchTextSplitter.Split("aaaa", "aa");

        Assert.NotNull(segments);
        AssertCoversText("aaaa", segments);
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.True(s.IsMatch));
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        var segments = SearchTextSplitter.Split("Hello", "hello");

        Assert.NotNull(segments);
        Assert.Single(segments);
        Assert.True(segments[0].IsMatch);
    }

    [Fact]
    public void TermEqualsWholeText_SingleMatchSegment()
    {
        var segments = SearchTextSplitter.Split("match", "match");

        Assert.NotNull(segments);
        Assert.Single(segments);
        Assert.True(segments[0].IsMatch);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(5, segments[0].Length);
    }
}
