using System;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;
using Avalonia.Media;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A swatch chip for a CSS colour string, with the colour in the other notation: a hex value
/// reads as <c>rgb(…)</c>, an <c>rgb()</c> or <c>hsl()</c> as hex - what is written is already on
/// the row, so the chip says what it does not.
/// </summary>
public sealed class ColourHintProvider : IValueHintProvider
{
    public bool IsActive => true;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (kind != JsonTokenKind.String || !ColourHintClassifier.TryClassify(rawValue, out uint argb, out bool isHex))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.Colour, argb, isHex ? (byte)1 : (byte)0);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueOffset)
    {
        var colour = Color.FromUInt32((uint)candidate.Payload);
        bool isHex = candidate.SchemeHint == 1;
        string text = isHex
            ? colour.A == 255 ? $"rgb({colour.R}, {colour.G}, {colour.B})" : $"rgba({colour.R}, {colour.G}, {colour.B}, {colour.A / 255.0:0.##})"
            : colour.A == 255 ? $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}" : $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{colour.A:X2}";
        return new ValueHint(text, TreeRunIcon.None, Swatch: colour);
    }

    /// <summary>A colour does not depend on any setting, so its hints never go stale.</summary>
    public event EventHandler? HintsChanged
    {
        add { }
        remove { }
    }
}
