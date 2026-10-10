using System;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A chip for a cron expression: the schedule in words and when it next runs. A cron line does
/// not say what zone it runs in, so the next run is worked out in the zone the date chips use.
/// </summary>
public sealed class CronHintProvider : IValueHintProvider
{
    private readonly DateHintSettings settings;
    private readonly TimeProvider clock;

    public CronHintProvider(DateHintSettings settings, TimeProvider clock)
    {
        this.settings = settings;
        this.clock = clock;
        settings.HintsChanged += (_, e) => HintsChanged?.Invoke(this, e);
    }

    public bool IsActive => true;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (kind != JsonTokenKind.String || !CronHintClassifier.TryParse(rawValue, out _))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.Cron, 0, 0);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueOffset)
    {
        if (!CronHintClassifier.TryParse(rawValue, out var schedule))
            return null;

        var now = settings.TimeZoneMode == DateHintTimeZoneMode.Utc
            ? clock.GetUtcNow().UtcDateTime
            : TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone).DateTime;
        string text = CronDescription.Describe(schedule);
        if (schedule.NextAfter(now) is { } next)
            text += $" · next {RelativeTime.Describe(next - now)}";

        return new ValueHint(text, TreeRunIcon.Time);
    }

    public event EventHandler? HintsChanged;
}
