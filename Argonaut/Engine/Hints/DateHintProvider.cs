using System;

namespace Argonaut.Engine.Hints;

/// <summary>
/// Composes DateHintClassifier + DateHintDecoder + DateHintSettings into an IValueHintProvider.
/// </summary>
public sealed class DateHintProvider : IValueHintProvider
{
    private readonly IDateHintSettings settings;

    public DateHintProvider(IDateHintSettings settings)
    {
        this.settings = settings;
        settings.HintsChanged += (_, e) => HintsChanged?.Invoke(this, e);
    }

    public bool IsActive => settings.FileDefaultScheme != DateDecodingScheme.Off;

    public bool TryClassify(ValueShape shape, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate)
    {
        candidate = default;

        if (shape != ValueShape.Number)
            return false;

        if (!DateHintClassifier.TryClassify(rawValue, out long value, out var scheme))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.Date, value, (byte)scheme);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset)
    {
        if (settings.FileDefaultScheme == DateDecodingScheme.Off)
            return null;

        // The chip opens the scheme menu for this value, so it stays clickable even when the
        // value's own scheme is Off - an em dash keeps the menu reachable.
        var effective = settings.GetEffectiveScheme(valueOffset);
        string text = effective == DateDecodingScheme.Off
            ? "—"
            : DateHintDecoder.Format(candidate.Payload, effective, settings.TimeZoneMode) ?? "out of range";
        return new ValueHint(ValueHintKind.Date, text, ValueHintSymbol.Time);
    }

    public event EventHandler? HintsChanged;
}
