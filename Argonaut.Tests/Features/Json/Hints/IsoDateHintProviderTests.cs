using System.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Hints;

public class IsoDateHintProviderTests
{
    /// <summary>Now is 2026-10-10 12:00 UTC, in a zone an hour ahead of UTC.</summary>
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("UTC+1 for tests", TimeSpan.FromHours(1), "UTC+1", "UTC+1");
    }

    private static string? Hint(string value, DateHintTimeZoneMode mode = DateHintTimeZoneMode.Utc)
    {
        var settings = new DateHintSettings();
        settings.SetTimeZoneMode(mode);
        var provider = new IsoDateHintProvider(settings, new FixedClock());
        byte[] raw = Encoding.UTF8.GetBytes(value);
        return provider.TryClassify(JsonTokenKind.String, raw, raw.Length, out var candidate)
            ? provider.FormatHint(candidate, raw, raw.Length, 0)?.Text
            : null;
    }

    [Fact]
    public void AnInstant_ShowsTheTimeInTheChosenZone_AndHowLongAgo()
        => Assert.Equal("2026-10-07 10:00:00 UTC · 3 days ago", Hint("2026-10-07T11:00:00+01:00"));

    [Fact]
    public void AnInstantWrittenInTheZoneShown_ShowsOnlyHowLongAgo()
    {
        Assert.Equal("3 days ago", Hint("2026-10-07T10:00:00Z"));
        Assert.Equal("in 4 months", Hint("2027-02-10T13:00:00+01:00", DateHintTimeZoneMode.Local));
    }

    [Fact]
    public void ADay_IsCountedInCalendarDays()
    {
        Assert.Equal("yesterday", Hint("2026-10-09"));
        Assert.Equal("today", Hint("2026-10-10"));
    }

    [Fact]
    public void ATimeWithNoZone_IsTheViewersOwn()
        => Assert.Equal("in 2 hours", Hint("2026-10-10T15:00"));

    [Fact]
    public void OnlyStringsAreRead()
    {
        var provider = new IsoDateHintProvider(new DateHintSettings(), new FixedClock());
        Assert.False(provider.TryClassify(JsonTokenKind.Number, Encoding.UTF8.GetBytes("2026-10-10"), 10, out _));
    }
}
