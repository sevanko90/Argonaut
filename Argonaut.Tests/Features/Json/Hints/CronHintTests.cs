using System.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Hints;

public class CronHintTests
{
    private static CronSchedule? Parse(string text)
        => CronHintClassifier.TryParse(Encoding.UTF8.GetBytes(text), out var schedule) ? schedule : null;

    [Theory]
    [InlineData("*/15 * * * *", "every 15 min")]
    [InlineData("0,15,30,45 * * * *", "every 15 min")]
    [InlineData("* * * * *", "every minute")]
    [InlineData("0 9 * * 1-5", "at 09:00, Mon–Fri")]
    [InlineData("0 9 * * MON-FRI", "at 09:00, Mon–Fri")]
    [InlineData("30 2 1 * *", "at 02:30 on day 1")]
    [InlineData("0 0 * * 0", "at 00:00, Sun")]
    [InlineData("0 0 * * 7", "at 00:00, Sun")]
    [InlineData("15 14 1 1 *", "at 14:15 on day 1 in Jan")]
    [InlineData("15 * * * *", "hourly at :15")]
    [InlineData("0 */2 * * *", "at :00 every 2 hours")]
    [InlineData("0 9,17 * * *", "at 09:00, 17:00")]
    [InlineData("0 0 1,15 * *", "at 00:00 on days 1, 15")]
    [InlineData("0 0 * JAN-MAR *", "at 00:00 in Jan–Mar")]
    [InlineData("0 12 * * MON,WED,FRI", "at 12:00, Mon, Wed, Fri")]
    [InlineData("@daily", "at 00:00")]
    [InlineData("@weekly", "at 00:00, Sun")]
    [InlineData("@hourly", "hourly at :00")]
    public void ASchedule_ReadsInWords(string text, string expected)
        => Assert.Equal(expected, CronDescription.Describe(Parse(text)!.Value));

    [Theory]
    [InlineData("1 2 3 4 5")]        // valid, but five plain numbers are more often something else
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("*  * * * *")]
    [InlineData("* * * * FOO")]
    [InlineData("1, * * * *")]
    [InlineData("@reboot")]
    [InlineData("every day")]
    public void AnythingElse_IsNotASchedule(string text) => Assert.Null(Parse(text));

    [Fact]
    public void TheNextRun_IsTheFirstMatchingMinuteAfterNow()
    {
        var friday = new DateTime(2026, 10, 9, 18, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0), Parse("0 9 * * 1-5")!.Value.NextAfter(friday));
        Assert.Equal(new DateTime(2026, 10, 9, 18, 15, 0), Parse("*/15 * * * *")!.Value.NextAfter(friday));
        Assert.Null(Parse("0 0 30 2 *")!.Value.NextAfter(friday));
    }

    /// <summary>Day of month and day of week both restricted: a day matching either runs, as in
    /// Vixie cron.</summary>
    [Fact]
    public void BothDayFieldsRestricted_RunsOnEither()
        => Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0), Parse("0 0 13 * MON")!.Value.NextAfter(new DateTime(2026, 10, 10, 12, 0, 0)));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void TheChip_ReadsTheScheduleAndWhenItNextRuns()
    {
        var settings = new DateHintSettings();
        settings.SetTimeZoneMode(DateHintTimeZoneMode.Utc);
        var provider = new CronHintProvider(settings, new FixedClock());
        byte[] raw = "0 9 * * 1-5"u8.ToArray();
        Assert.True(provider.TryClassify(JsonTokenKind.String, raw, out var candidate));
        Assert.Equal("at 09:00, Mon–Fri · next in 2 days", provider.FormatHint(candidate, raw, 0)?.Text);
    }
}
