using System;

namespace Argonaut.Engine.Hints;

/// <summary>
/// A swatch chip for a CSS colour string, with the colour in the other notation: a hex value
/// reads as <c>rgb(…)</c>, an <c>rgb()</c> or <c>hsl()</c> as hex - what is written is already on
/// the row, so the chip says what it does not.
/// </summary>
public sealed class ColourHintProvider : IValueHintProvider
{
    public bool IsActive => true;

    public bool TryClassify(ValueShape shape, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (shape != ValueShape.String || !ColourHintClassifier.TryClassify(rawValue, out uint argb, out bool isHex))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.Colour, argb, isHex ? (byte)1 : (byte)0);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset)
    {
        uint argb = (uint)candidate.Payload;
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        bool isHex = candidate.SchemeHint == 1;
        string text = isHex
            ? a == 255 ? $"rgb({r}, {g}, {b})" : $"rgba({r}, {g}, {b}, {a / 255.0:0.##})"
            : a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{r:X2}{g:X2}{b:X2}{a:X2}";
        return new ValueHint(ValueHintKind.Colour, text, ValueHintSymbol.None, Colour: argb);
    }

    /// <summary>A colour does not depend on any setting, so its hints never go stale.</summary>
    public event EventHandler? HintsChanged
    {
        add { }
        remove { }
    }
}
