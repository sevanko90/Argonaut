using System.Text;
using Argonaut.Features.Json.Hints;

namespace Argonaut.Tests.Features.Json.Hints;

public class IsoDateHintClassifierTests
{
    private static (bool Ok, long Ticks, IsoDateShape Shape, short Offset) Classify(string text)
    {
        bool ok = IsoDateHintClassifier.TryClassify(Encoding.UTF8.GetBytes(text), out long ticks, out var shape, out short offset);
        return (ok, ticks, shape, offset);
    }

    [Fact]
    public void ADateAlone_IsACalendarDay()
    {
        var result = Classify("2026-10-07");
        Assert.True(result.Ok);
        Assert.Equal(IsoDateShape.Date, result.Shape);
        Assert.Equal(new DateTime(2026, 10, 7).Ticks, result.Ticks);
    }

    [Theory]
    [InlineData("2026-10-07T09:15", 9, 15, 0, 0)]
    [InlineData("2026-10-07 09:15:30", 9, 15, 30, 0)]
    [InlineData("2026-10-07T09:15:30.5", 9, 15, 30, 5_000_000)]
    [InlineData("2026-10-07T09:15:30.1234567", 9, 15, 30, 1_234_567)]
    public void ATimeWithNoZone_IsAWallClockTime(string text, int hour, int minute, int second, long fractionTicks)
    {
        var result = Classify(text);
        Assert.True(result.Ok);
        Assert.Equal(IsoDateShape.LocalDateTime, result.Shape);
        Assert.Equal(new DateTime(2026, 10, 7, hour, minute, second).Ticks + fractionTicks, result.Ticks);
    }

    [Theory]
    [InlineData("2026-10-07T09:15:00Z", 0)]
    [InlineData("2026-10-07T09:15:00+01:00", 60)]
    [InlineData("2026-10-07T09:15:00+0530", 330)]
    [InlineData("2026-10-07T09:15:00-08", -480)]
    [InlineData("2026-10-07T09:15:00.123Z", 0)]
    public void AZoneMakesItAnInstant_InUtcTicks(string text, int offsetMinutes)
    {
        var result = Classify(text);
        Assert.True(result.Ok);
        Assert.Equal(IsoDateShape.Instant, result.Shape);
        Assert.Equal(offsetMinutes, result.Offset);
        var expected = DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture).UtcTicks;
        Assert.Equal(expected, result.Ticks);
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    [InlineData("2026-1-01")]
    [InlineData("2026-10-07T24:00")]
    [InlineData("2026-10-07T09:60")]
    [InlineData("2026-10-07T09:15:61")]
    [InlineData("2026-10-07T09:15:00.12345678")]
    [InlineData("2026-10-07T09:15:00.")]
    [InlineData("2026-10-07T09:15:00+15:00")]
    [InlineData("2026-10-07T09:15:00Zjunk")]
    [InlineData("2026-10-07x")]
    [InlineData("2026-10-07T09")]
    [InlineData("hello")]
    [InlineData("20261007")]
    public void AnythingElse_IsNotADate(string text) => Assert.False(Classify(text).Ok);
}
