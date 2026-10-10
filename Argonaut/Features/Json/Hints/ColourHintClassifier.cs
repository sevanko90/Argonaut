using System;
using System.Buffers.Text;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Recognises a CSS colour in a JSON string's raw bytes: <c>#RRGGBB</c> or <c>#RRGGBBAA</c>, and <c>rgb()</c>/<c>rgba()</c> and <c>hsl()</c>/<c>hsla()</c>
/// with comma or space separated arguments, an optional alpha after a comma or a slash, and
/// percentages where CSS allows them. Allocation-free; anything out of range, or any byte out of
/// place, and it is not a colour. The three- and four-digit hex shorthands are valid CSS but rare
/// in data, and as often something else - <c>"#123"</c> is more likely an issue number - so they
/// are not read.
/// </summary>
public static class ColourHintClassifier
{
    public static bool TryClassify(ReadOnlySpan<byte> text, out uint argb, out bool isHex)
    {
        argb = 0;
        isHex = text.Length > 0 && text[0] == '#';
        if (isHex)
            return TryHex(text[1..], out argb);

        if (TryFunction(text, "rgba"u8, out var arguments) || TryFunction(text, "rgb"u8, out arguments))
            return TryRgb(arguments, out argb);
        if (TryFunction(text, "hsla"u8, out arguments) || TryFunction(text, "hsl"u8, out arguments))
            return TryHsl(arguments, out argb);
        return false;
    }

    private static bool TryHex(ReadOnlySpan<byte> digits, out uint argb)
    {
        argb = 0;
        if (digits.Length is not (6 or 8))
            return false;

        uint value = 0;
        foreach (byte b in digits)
        {
            int nibble = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (nibble < 0)
                return false;
            value = (value << 4) | (uint)nibble;
        }

        argb = digits.Length == 8 ? (value << 24 | value >> 8) : 0xFF000000 | value;
        return true;
    }

    /// <summary>The text between <c>name(</c> and a closing <c>)</c> that ends the value.</summary>
    private static bool TryFunction(ReadOnlySpan<byte> text, ReadOnlySpan<byte> name, out ReadOnlySpan<byte> arguments)
    {
        arguments = default;
        if (text.Length < name.Length + 2 || !text.StartsWith(name) || text[name.Length] != '(' || text[^1] != ')')
            return false;

        arguments = text[(name.Length + 1)..^1];
        return true;
    }

    private static bool TryRgb(ReadOnlySpan<byte> arguments, out uint argb)
    {
        argb = 0;
        Span<Argument> parts = stackalloc Argument[4];
        if (!TryArguments(arguments, parts, out int count) || count < 3)
            return false;

        Span<int> channels = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            double channel = parts[i].Unit == Unit.Percent ? parts[i].Number * 2.55 : parts[i].Number;
            if (parts[i].Unit == Unit.Degrees || channel is < 0 or > 255.0001)
                return false;
            channels[i] = (int)Math.Round(channel);
        }

        if (!TryAlpha(parts, count, out int alpha))
            return false;

        argb = (uint)alpha << 24 | (uint)channels[0] << 16 | (uint)channels[1] << 8 | (uint)channels[2];
        return true;
    }

    private static bool TryHsl(ReadOnlySpan<byte> arguments, out uint argb)
    {
        argb = 0;
        Span<Argument> parts = stackalloc Argument[4];
        if (!TryArguments(arguments, parts, out int count) || count < 3 || parts[0].Unit == Unit.Percent
            || parts[1].Unit != Unit.Percent || parts[2].Unit != Unit.Percent)
            return false;

        double hue = ((parts[0].Number % 360) + 360) % 360;
        double saturation = parts[1].Number / 100, lightness = parts[2].Number / 100;
        if (saturation is < 0 or > 1 || lightness is < 0 or > 1 || !TryAlpha(parts, count, out int alpha))
            return false;

        // CSS Color 4's hsl-to-rgb.
        double a = saturation * Math.Min(lightness, 1 - lightness);
        int Channel(double n)
        {
            double k = (n + hue / 30) % 12;
            return (int)Math.Round(255 * (lightness - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1))));
        }

        argb = (uint)alpha << 24 | (uint)Channel(0) << 16 | (uint)Channel(8) << 8 | (uint)Channel(4);
        return true;
    }

    private static bool TryAlpha(ReadOnlySpan<Argument> parts, int count, out int alpha)
    {
        alpha = 255;
        if (count < 4)
            return true;

        double value = parts[3].Unit == Unit.Percent ? parts[3].Number / 100 : parts[3].Number;
        if (parts[3].Unit == Unit.Degrees || value is < 0 or > 1)
            return false;

        alpha = (int)Math.Round(value * 255);
        return true;
    }

    private enum Unit : byte { None, Percent, Degrees }

    private readonly record struct Argument(double Number, Unit Unit);

    /// <summary>Up to four numbers, each optionally a percentage or in degrees, separated by
    /// commas or spaces, the fourth optionally after a slash.</summary>
    private static bool TryArguments(ReadOnlySpan<byte> text, Span<Argument> parts, out int count)
    {
        count = 0;
        int at = 0;
        while (true)
        {
            while (at < text.Length && text[at] == ' ')
                at++;
            if (at == text.Length)
                return count > 0;
            if (count == parts.Length)
                return false;

            if (count > 0)
            {
                if (text[at] is (byte)',' or (byte)'/')
                {
                    at++;
                    while (at < text.Length && text[at] == ' ')
                        at++;
                }
                else if (text[at - 1] != ' ')
                {
                    return false;
                }
            }

            int start = at;
            while (at < text.Length && (text[at] is >= (byte)'0' and <= (byte)'9' || text[at] == '.' || (at == start && text[at] is (byte)'-' or (byte)'+')))
                at++;
            if (at == start || !Utf8Parser.TryParse(text[start..at], out double number, out int consumed) || consumed != at - start)
                return false;

            var unit = Unit.None;
            if (at < text.Length && text[at] == '%')
            {
                unit = Unit.Percent;
                at++;
            }
            else if (text[at..].StartsWith("deg"u8))
            {
                unit = Unit.Degrees;
                at += 3;
            }

            parts[count++] = new Argument(number, unit);
        }
    }
}
