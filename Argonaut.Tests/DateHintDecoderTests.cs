using System;
using System.Globalization;
using Argonaut.Features.Json.Hints;

namespace Argonaut.Tests;

/// <summary>
/// Timezone-agnostic: never hard-codes a specific local-time string, since that would depend
/// on the machine running the test.
/// </summary>
public class DateHintDecoderTests
{
    [Fact]
    public void JsSeconds_ConvertsToMilliseconds()
    {
        Assert.True(DateHintDecoder.TryToUnixMilliseconds(1709305509, DateDecodingScheme.JsSeconds, out long unixMs));
        Assert.Equal(1709305509000, unixMs);
    }

    [Fact]
    public void JsMilliseconds_PassesThrough()
    {
        Assert.True(DateHintDecoder.TryToUnixMilliseconds(1709305509000, DateDecodingScheme.JsMilliseconds, out long unixMs));
        Assert.Equal(1709305509000, unixMs);
    }

    [Fact]
    public void KeepaMinutes_ConvertsUsingEpochOffset()
    {
        const long keepaMinutes = 7_212_300;
        Assert.True(DateHintDecoder.TryToUnixMilliseconds(keepaMinutes, DateDecodingScheme.KeepaMinutes, out long unixMs));
        Assert.Equal((keepaMinutes + DateHintDecoder.KeepaEpochOffsetMinutes) * 60_000, unixMs);
    }

    [Fact]
    public void Off_AlwaysFails()
    {
        Assert.False(DateHintDecoder.TryToUnixMilliseconds(1709305509, DateDecodingScheme.Off, out _));
    }

    [Fact]
    public void OutOfRangeOverride_FailsWithoutThrowing()
    {
        // A 13-digit JS-millisecond-sized value interpreted as Keepa minutes overflows far
        // past any representable DateTimeOffset - must return false, never throw.
        Assert.False(DateHintDecoder.TryToUnixMilliseconds(1709305509000, DateDecodingScheme.KeepaMinutes, out _));
    }

    [Fact]
    public void Format_Local_UsesTheMachinesConfiguredDateTimeFormat()
    {
        // "Local" means the current culture's own short-date + long-time pattern (12-hour
        // with AM/PM on a US machine, 24-hour on a UK machine), not a fixed ISO-ish shape.
        const long unixSeconds = 1709305509;
        string? formatted = DateHintDecoder.Format(unixSeconds, DateDecodingScheme.JsSeconds, DateHintTimeZoneMode.Local);

        Assert.NotNull(formatted);

        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixSeconds * 1000).ToLocalTime();
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        string expectedTimestamp = local.ToString($"{dtf.ShortDatePattern} {dtf.LongTimePattern}", CultureInfo.CurrentCulture);

        Assert.StartsWith(expectedTimestamp, formatted);
    }

    [Fact]
    public void Format_Local_OffsetLabel_ReflectsTheDecodedInstantsOwnDstRule_NotNow()
    {
        // Regression for the "why does this look like UTC" report: a December UTC instant is
        // GMT (UTC+0) in the UK regardless of what DST rule is active right now, so the label
        // must reflect the offset AT the decoded instant, not the current moment's offset.
        var winterUtc = new DateTimeOffset(2023, 12, 10, 23, 40, 0, TimeSpan.Zero);
        string? formatted = DateHintDecoder.Format(winterUtc.ToUnixTimeSeconds(), DateDecodingScheme.JsSeconds, DateHintTimeZoneMode.Local);

        Assert.NotNull(formatted);

        var expectedOffset = TimeZoneInfo.Local.GetUtcOffset(winterUtc);
        var local = winterUtc.ToOffset(expectedOffset);
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        string expectedTimestamp = local.ToString($"{dtf.ShortDatePattern} {dtf.LongTimePattern}", CultureInfo.CurrentCulture);

        Assert.StartsWith(expectedTimestamp, formatted);
    }

    [Fact]
    public void Format_Utc_UsesDisplayFriendlyIsoShape_RegardlessOfCulture()
    {
        var winterUtc = new DateTimeOffset(2023, 12, 10, 23, 40, 0, TimeSpan.Zero);
        string? formatted = DateHintDecoder.Format(winterUtc.ToUnixTimeSeconds(), DateDecodingScheme.JsSeconds, DateHintTimeZoneMode.Utc);

        Assert.Equal("2023-12-10 23:40:00 [UTC]", formatted);
    }

    [Fact]
    public void Format_Utc_NeverAppliesTheMachinesTimeZoneOffset()
    {
        // A summer instant would shift under Format(..., Local) if the machine isn't UTC -
        // Utc mode must reproduce the raw UTC calendar fields unchanged.
        var summerUtc = new DateTimeOffset(2024, 6, 15, 14, 5, 9, TimeSpan.Zero);
        string? formatted = DateHintDecoder.Format(summerUtc.ToUnixTimeSeconds(), DateDecodingScheme.JsSeconds, DateHintTimeZoneMode.Utc);

        Assert.Equal("2024-06-15 14:05:09 [UTC]", formatted);
    }

    [Fact]
    public void Format_NullWhenConversionFails()
    {
        Assert.Null(DateHintDecoder.Format(1709305509, DateDecodingScheme.Off, DateHintTimeZoneMode.Local));
        Assert.Null(DateHintDecoder.Format(1709305509, DateDecodingScheme.Off, DateHintTimeZoneMode.Utc));
    }
}
