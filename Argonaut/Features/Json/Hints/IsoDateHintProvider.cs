using System;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A chip for an ISO 8601 date string: how long ago or how far off it is, and for an instant the
/// time in the chosen zone (local or UTC, the date chip's setting) - left out when it would only
/// repeat what is written, an offset matching the zone shown. A calendar day or a time with no
/// zone is read as the viewer's own, so it gets the relative reading alone. Unlike a number,
/// such a string is unambiguous, so it needs no scheme and is always on.
/// </summary>
public sealed class IsoDateHintProvider : IValueHintProvider
{
    private readonly DateHintSettings settings;
    private readonly TimeProvider clock;

    public IsoDateHintProvider(DateHintSettings settings, TimeProvider clock)
    {
        this.settings = settings;
        this.clock = clock;
        settings.HintsChanged += (_, e) => HintsChanged?.Invoke(this, e);
    }

    public bool IsActive => true;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (kind != JsonTokenKind.String || !IsoDateHintClassifier.TryClassify(rawValue, out long ticks, out var shape, out short offset))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.IsoDate, ticks, (byte)shape, offset);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, long valueOffset)
    {
        var shape = (IsoDateShape)candidate.SchemeHint;
        if (shape == IsoDateShape.Date)
        {
            int days = (int)((new DateTime(candidate.Payload) - clock.GetLocalNow().Date).TotalDays);
            return new ValueHint(RelativeTime.DescribeDays(days), TreeRunIcon.Time);
        }

        if (shape == IsoDateShape.LocalDateTime)
            return new ValueHint(RelativeTime.Describe(new DateTime(candidate.Payload) - clock.GetLocalNow().DateTime), TreeRunIcon.Time);

        var instant = new DateTimeOffset(candidate.Payload, TimeSpan.Zero);
        string relative = RelativeTime.Describe(instant - clock.GetUtcNow());
        var shownOffset = settings.TimeZoneMode == DateHintTimeZoneMode.Utc
            ? TimeSpan.Zero
            : TimeZoneInfo.ConvertTime(instant, clock.LocalTimeZone).Offset;

        return shownOffset == TimeSpan.FromMinutes(candidate.OffsetMinutes)
            ? new ValueHint(relative, TreeRunIcon.Time)
            : new ValueHint($"{DateHintDecoder.FormatInstant(instant, settings.TimeZoneMode, clock.LocalTimeZone)} · {relative}", TreeRunIcon.Time);
    }

    public event EventHandler? HintsChanged;
}
