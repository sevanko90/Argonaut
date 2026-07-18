using System;
using Argonaut.Features.Json;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Composes DateHintClassifier + DateHintDecoder + DateHintSettings into an IValueHintProvider.
/// </summary>
public sealed class DateHintProvider : IValueHintProvider
{
    private readonly DateHintSettings settings;

    public DateHintProvider(DateHintSettings settings)
    {
        this.settings = settings;
        settings.HintsChanged += (_, e) => HintsChanged?.Invoke(this, e);
    }

    public bool IsActive => settings.FileDefaultScheme != DateDecodingScheme.Off;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, out ValueHintCandidate candidate)
    {
        candidate = default;

        if (kind != JsonTokenKind.Number)
            return false;

        if (!DateHintClassifier.TryClassify(rawValue, out long value, out var scheme))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.Date, value, (byte)scheme);
        return true;
    }

    public string? FormatHint(in ValueHintCandidate candidate, int tokenIndex)
    {
        if (settings.FileDefaultScheme == DateDecodingScheme.Off)
            return null;

        var effective = settings.GetEffectiveScheme(tokenIndex);
        if (effective == DateDecodingScheme.Off)
            return "—"; // em dash - a clickable placeholder so the flyout stays reachable

        return DateHintDecoder.Format(candidate.Payload, effective, settings.TimeZoneMode) ?? "out of range";
    }

    public event EventHandler? HintsChanged;
}
