using Argonaut.Features.Json.Hints;

namespace Argonaut.Tests.Features.Json.Hints;

public class RelativeTimeTests
{
    [Theory]
    [InlineData(-20, "just now")]
    [InlineData(30, "in under a minute")]
    [InlineData(-5 * 60, "5 min ago")]
    [InlineData(60, "in 1 min")]
    [InlineData(3 * 3600, "in 3 hours")]
    [InlineData(-3600, "1 hour ago")]
    [InlineData(-3 * 86400, "3 days ago")]
    [InlineData(40 * 86400, "in 1 month")]
    [InlineData(-122 * 86400, "4 months ago")]
    [InlineData(330 * 86400, "in 1 year")]
    [InlineData(-2 * 365 * 86400, "2 years ago")]
    public void ATime_ReadsAsAPersonWouldSayIt(int seconds, string expected)
        => Assert.Equal(expected, RelativeTime.Describe(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "today")]
    [InlineData(-1, "yesterday")]
    [InlineData(1, "tomorrow")]
    [InlineData(-3, "3 days ago")]
    [InlineData(125, "in 4 months")]
    public void ADay_ReadsAsAPersonWouldSayIt(int days, string expected)
        => Assert.Equal(expected, RelativeTime.DescribeDays(days));
}
