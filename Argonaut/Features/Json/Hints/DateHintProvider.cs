using System;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Json.Tree;
using Argonaut.Ui.Tree;

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

    public ValueHint? FormatHint(in ValueHintCandidate candidate, long valueOffset)
    {
        if (settings.FileDefaultScheme == DateDecodingScheme.Off)
            return null;

        // The chip opens the scheme menu for this value, so it stays clickable even when the
        // value's own scheme is Off - an em dash keeps the menu reachable.
        var effective = settings.GetEffectiveScheme(valueOffset);
        string text = effective == DateDecodingScheme.Off
            ? "—"
            : DateHintDecoder.Format(candidate.Payload, effective, settings.TimeZoneMode) ?? "out of range";
        return new ValueHint(text, TreeRunIcon.Time, new DateSchemeLink(valueOffset));
    }

    public event EventHandler? HintsChanged;
}
