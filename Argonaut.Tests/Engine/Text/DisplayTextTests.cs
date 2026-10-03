using Argonaut.Engine.Text;

namespace Argonaut.Tests.Engine.Text;

public sealed class DisplayTextTests
{
    [Fact]
    public void BreakLongRuns_LeavesTextWithShortRunsAsItIs()
    {
        string text = "lorem ipsum dolor sit amet";

        Assert.Same(text, DisplayText.BreakLongRuns(text, maxRun: 5));
    }

    [Fact]
    public void BreakLongRuns_CutsAnUnbrokenRunIntoParagraphsOfAtMostTheRun()
    {
        string broken = DisplayText.BreakLongRuns(new string('x', 10), maxRun: 4);

        Assert.Equal("xxxx\nxxxx\nxx", broken);
    }

    [Fact]
    public void BreakLongRuns_CountsEachRunFromTheLastWhitespace()
    {
        Assert.Equal("xxx xxx\nx", DisplayText.BreakLongRuns("xxx xxxx", maxRun: 3));
    }

    [Fact]
    public void BreakLongRuns_RemovingTheBreaksGivesBackTheText()
    {
        string text = string.Concat(Enumerable.Repeat("ab cdefghijklmnop\tqrstuvwxyz0123456789", 50));

        Assert.Equal(text, DisplayText.BreakLongRuns(text, maxRun: 7).Replace("\n", ""));
    }

    [Theory]
    [InlineData("xxx\U0001F600x")]     // a surrogate pair straddling the cap
    [InlineData("xxxéx")]        // a combining acute accent after the cap
    [InlineData("xx\U0001F468‍\U0001F469x")] // a joined emoji sequence
    [InlineData("xxx❤️x")]   // a variation selector after the cap
    public void BreakLongRuns_NeverSplitsACharacter(string text)
    {
        string broken = DisplayText.BreakLongRuns(text, maxRun: 3);

        int cut = broken.IndexOf('\n');
        Assert.True(cut > 0);
        Assert.False(char.IsLowSurrogate(broken[cut + 1]));
        Assert.NotEqual('‍', broken[cut - 1]);
        Assert.NotEqual('‍', broken[cut + 1]);
        Assert.NotEqual('́', broken[cut + 1]);
        Assert.NotEqual('️', broken[cut + 1]);
        Assert.Equal(text, broken.Replace("\n", ""));
    }
}
