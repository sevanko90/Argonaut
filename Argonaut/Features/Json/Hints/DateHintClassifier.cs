using System;
using System.Buffers.Text;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Classifies a JSON number's raw digit text as a date candidate purely from its length:
/// 7-8 digits => Keepa minutes, 9-10 => JS seconds, 11-13 => JS milliseconds. Any non-digit
/// character (sign, decimal point, exponent) disqualifies the value. Leading zeros are
/// accepted by this rule even though they can't occur in valid JSON numbers.
/// </summary>
public static class DateHintClassifier
{
    private const int MinDigits = 7;
    private const int MaxDigits = 13;
    private const int KeepaMaxDigits = 8;
    private const int JsSecondsMaxDigits = 10;

    public static bool TryClassify(ReadOnlySpan<byte> rawNumber, out long value, out DateDecodingScheme scheme)
    {
        value = 0;
        scheme = DateDecodingScheme.Off;

        int length = rawNumber.Length;
        if (length < MinDigits || length > MaxDigits)
            return false;

        for (int i = 0; i < length; i++)
        {
            byte b = rawNumber[i];
            if (b < (byte)'0' || b > (byte)'9')
                return false;
        }

        if (!Utf8Parser.TryParse(rawNumber, out long parsed, out int consumed) || consumed != length)
            return false;

        value = parsed;
        scheme = length <= KeepaMaxDigits ? DateDecodingScheme.KeepaMinutes
            : length <= JsSecondsMaxDigits ? DateDecodingScheme.JsSeconds
            : DateDecodingScheme.JsMilliseconds;
        return true;
    }
}
