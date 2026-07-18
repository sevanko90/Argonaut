using System.Text;
using Argonaut.Features.Json.Hints;

namespace Argonaut.Tests;

public class DateHintClassifierTests
{
    private static bool Classify(string digits, out long value, out DateDecodingScheme scheme)
        => DateHintClassifier.TryClassify(Encoding.ASCII.GetBytes(digits), out value, out scheme);

    [Theory]
    [InlineData("123456", false, default(DateDecodingScheme))]      // 6 digits - too short
    [InlineData("1234567", true, DateDecodingScheme.KeepaMinutes)]  // 7 digits
    [InlineData("12345678", true, DateDecodingScheme.KeepaMinutes)] // 8 digits
    [InlineData("123456789", true, DateDecodingScheme.JsSeconds)]   // 9 digits
    [InlineData("1234567890", true, DateDecodingScheme.JsSeconds)]  // 10 digits
    [InlineData("12345678901", true, DateDecodingScheme.JsMilliseconds)]     // 11 digits
    [InlineData("1234567890123", true, DateDecodingScheme.JsMilliseconds)]   // 13 digits
    [InlineData("12345678901234", false, default(DateDecodingScheme))]       // 14 digits - too long
    public void ClassifiesByDigitLength(string digits, bool expected, DateDecodingScheme expectedScheme)
    {
        bool result = Classify(digits, out long value, out var scheme);

        Assert.Equal(expected, result);
        if (expected)
        {
            Assert.Equal(expectedScheme, scheme);
            Assert.Equal(long.Parse(digits), value);
        }
    }

    [Theory]
    [InlineData("-123456789")]  // sign
    [InlineData("12345.6789")]  // decimal point
    [InlineData("1234567e89")]  // exponent
    [InlineData("")]            // empty
    public void RejectsNonDigitContent(string raw)
    {
        Assert.False(Classify(raw, out _, out _));
    }

    [Fact]
    public void LeadingZeros_AreClassified()
    {
        // Valid JSON numbers can never have leading zeros, but the digits-only rule accepts
        // them by design since the classifier only inspects raw bytes.
        Assert.True(Classify("0001234567", out long value, out var scheme));
        Assert.Equal(DateDecodingScheme.JsSeconds, scheme);
        Assert.Equal(1234567, value);
    }
}
