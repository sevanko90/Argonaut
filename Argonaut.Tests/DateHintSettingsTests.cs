using Argonaut.Features.Json.Hints;

namespace Argonaut.Tests;

public class DateHintSettingsTests
{
    [Fact]
    public void EffectiveScheme_DefaultsToFileDefault()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.JsSeconds);

        Assert.Equal(DateDecodingScheme.JsSeconds, settings.GetEffectiveScheme(5));
    }

    [Fact]
    public void TokenOverride_WinsOverFileDefault()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.JsSeconds);
        settings.SetTokenOverride(5, DateDecodingScheme.KeepaMinutes);

        Assert.Equal(DateDecodingScheme.KeepaMinutes, settings.GetEffectiveScheme(5));
        Assert.Equal(DateDecodingScheme.JsSeconds, settings.GetEffectiveScheme(6)); // sibling unaffected
    }

    [Fact]
    public void ClearingOverride_RestoresFileDefault()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.JsSeconds);
        settings.SetTokenOverride(5, DateDecodingScheme.KeepaMinutes);
        settings.SetTokenOverride(5, null);

        Assert.Equal(DateDecodingScheme.JsSeconds, settings.GetEffectiveScheme(5));
    }

    [Fact]
    public void TrySetInferredDefault_SetsWhenUnset()
    {
        var settings = new DateHintSettings();

        Assert.True(settings.TrySetInferredDefault(DateDecodingScheme.KeepaMinutes));
        Assert.Equal(DateDecodingScheme.KeepaMinutes, settings.FileDefaultScheme);
    }

    [Fact]
    public void TrySetInferredDefault_NoOpsAfterUserSelection()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.Off);

        Assert.False(settings.TrySetInferredDefault(DateDecodingScheme.JsMilliseconds));
        Assert.Equal(DateDecodingScheme.Off, settings.FileDefaultScheme);
    }

    [Fact]
    public void TrySetInferredDefault_NoOpsAfterPriorSuccessfulInference()
    {
        var settings = new DateHintSettings();
        settings.TrySetInferredDefault(DateDecodingScheme.JsSeconds);

        Assert.False(settings.TrySetInferredDefault(DateDecodingScheme.KeepaMinutes));
        Assert.Equal(DateDecodingScheme.JsSeconds, settings.FileDefaultScheme);
    }

    [Fact]
    public void HintsChanged_FiresOnDefaultChange()
    {
        var settings = new DateHintSettings();
        int fired = 0;
        settings.HintsChanged += (_, _) => fired++;

        settings.SetUserDefault(DateDecodingScheme.JsSeconds);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void HintsChanged_FiresOnOverrideSetAndClear()
    {
        var settings = new DateHintSettings();
        int fired = 0;
        settings.HintsChanged += (_, _) => fired++;

        settings.SetTokenOverride(1, DateDecodingScheme.KeepaMinutes);
        settings.SetTokenOverride(1, null);

        Assert.Equal(2, fired);
    }

    [Fact]
    public void HintsChanged_DoesNotFireOnNoOpInferredSet()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.Off);
        int fired = 0;
        settings.HintsChanged += (_, _) => fired++;

        settings.TrySetInferredDefault(DateDecodingScheme.JsSeconds);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void HintsChanged_DoesNotFireOnEqualValueSet()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.JsSeconds);
        int fired = 0;
        settings.HintsChanged += (_, _) => fired++;

        settings.SetUserDefault(DateDecodingScheme.JsSeconds);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void TimeZoneMode_DefaultsToLocal()
    {
        var settings = new DateHintSettings();
        Assert.Equal(DateHintTimeZoneMode.Local, settings.TimeZoneMode);
    }

    [Fact]
    public void SetTimeZoneMode_UpdatesAndFiresHintsChanged()
    {
        var settings = new DateHintSettings();
        int fired = 0;
        settings.HintsChanged += (_, _) => fired++;

        settings.SetTimeZoneMode(DateHintTimeZoneMode.Utc);

        Assert.Equal(DateHintTimeZoneMode.Utc, settings.TimeZoneMode);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void SetTimeZoneMode_NoOpsOnEqualValueSet()
    {
        var settings = new DateHintSettings();
        settings.SetTimeZoneMode(DateHintTimeZoneMode.Utc);
        int fired = 0;
        settings.HintsChanged += (_, _) => fired++;

        settings.SetTimeZoneMode(DateHintTimeZoneMode.Utc);

        Assert.Equal(0, fired);
    }
}
