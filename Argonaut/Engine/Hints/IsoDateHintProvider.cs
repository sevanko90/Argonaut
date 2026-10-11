using System;

namespace Argonaut.Engine.Hints;

/// <summary>
/// A chip for an ISO 8601 date string: how long ago or how far off it is, and for an instant the
/// time in the chosen zone (local or UTC, the date chip's setting) - left out when it would only
/// repeat what is written, an offset matching the zone shown. A calendar day or a time with no
/// zone is read as the viewer's own, so it gets the relative reading alone. Unlike a number,
/// such a string is unambiguous, so it needs no scheme and is always on.
/// </summary>
public sealed class IsoDateHintProvider : IValueHintProvider
{
    private readonly IDateHintSettings settings;
    private readonly TimeProvider clock;

    public IsoDateHintProvider(IDateHintSettings settings, TimeProvider clock)
    {
        this.settings = settings;
        this.clock = clock;
        settings.HintsChanged += (_, e) => HintsChanged?.Invoke(this, e);
    }

    public bool IsActive => true;

    public bool TryClassify(ValueShape shape, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (shape != ValueShape.String || !IsoDateHintClassifier.TryClassify(rawValue, out long ticks, out var written, out short offset))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.IsoDate, ticks, (byte)written, offset);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset)
    {
        var shape = (IsoDateShape)candidate.SchemeHint;
        if (shape == IsoDateShape.Date)
        {
            int days = (int)((new DateTime(candidate.Payload) - clock.GetLocalNow().Date).TotalDays);
            return new ValueHint(ValueHintKind.IsoDate, RelativeTime.DescribeDays(days), ValueHintSymbol.Time);
        }

        if (shape == IsoDateShape.LocalDateTime)
            return new ValueHint(ValueHintKind.IsoDate, RelativeTime.Describe(new DateTime(candidate.Payload) - clock.GetLocalNow().DateTime), ValueHintSymbol.Time);

        var instant = new DateTimeOffset(candidate.Payload, TimeSpan.Zero);
        string relative = RelativeTime.Describe(instant - clock.GetUtcNow());
        var shownOffset = settings.TimeZoneMode == DateHintTimeZoneMode.Utc
            ? TimeSpan.Zero
            : TimeZoneInfo.ConvertTime(instant, clock.LocalTimeZone).Offset;

        return shownOffset == TimeSpan.FromMinutes(candidate.OffsetMinutes)
            ? new ValueHint(ValueHintKind.IsoDate, relative, ValueHintSymbol.Time)
            : new ValueHint(ValueHintKind.IsoDate, $"{DateHintDecoder.FormatInstant(instant, settings.TimeZoneMode, clock.LocalTimeZone)} · {relative}", ValueHintSymbol.Time);
    }

    public event EventHandler? HintsChanged;
}
