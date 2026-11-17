using System;
using System.Globalization;

namespace Argonaut.Features.Hex;

/// <summary>
/// Reads an offset typed into the hex view's go-to entry. Hex when it says so - a <c>0x</c>
/// prefix or an <c>h</c> suffix - or when it has a hex letter in it; decimal otherwise, so
/// <c>1000</c> is a thousand and <c>3E8</c> is too. A leading <c>+</c> or <c>-</c> counts from the
/// caret rather than the start. Spaces, <c>_</c> and <c>,</c> are ignored, so an offset copied
/// with its digits grouped reads as it looks.
/// </summary>
public static class HexOffsetEntry
{
    /// <summary>The offset <paramref name="text"/> names, counting from <paramref name="caret"/>
    /// when it is relative; false when it is no number, or a relative step would go before the
    /// start.</summary>
    public static bool TryParse(string? text, long caret, out long offset)
    {
        offset = 0;
        Span<char> digits = stackalloc char[Math.Min(64, text?.Length ?? 0)];
        int count = 0;
        foreach (char c in text ?? string.Empty)
        {
            if (c is ' ' or '_' or ',')
                continue;
            if (count == digits.Length)
                return false;
            digits[count++] = c;
        }

        ReadOnlySpan<char> number = digits[..count];
        int sign = 0;
        if (number.Length > 0 && number[0] is '+' or '-')
        {
            sign = number[0] == '+' ? 1 : -1;
            number = number[1..];
        }

        bool hex = false;
        if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = true;
            number = number[2..];
        }
        else if (number.Length > 0 && number[^1] is 'h' or 'H')
        {
            hex = true;
            number = number[..^1];
        }
        else
        {
            foreach (char c in number)
                hex |= c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
        }

        if (number.IsEmpty)
            return false;

        bool read = hex
            ? long.TryParse(number, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long value) && value >= 0
            : long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (!read)
            return false;

        if (sign == 0)
        {
            offset = value;
            return true;
        }

        long moved = caret + sign * value;
        if (moved < 0 || (sign > 0 && moved < caret))
            return false;

        offset = moved;
        return true;
    }
}
