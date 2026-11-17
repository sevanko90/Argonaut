using Argonaut.Features.Hex.Values;

namespace Argonaut.Tests.Features.Hex.Values;

public sealed class ByteReadingTests
{
    private static readonly byte[] Bytes = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x80];

    [Fact]
    public void Integers_ReadInEitherOrder()
    {
        Assert.Equal((ushort)0x0201, ByteReading.UInt16(Bytes, ByteOrder.Little));
        Assert.Equal((ushort)0x0102, ByteReading.UInt16(Bytes, ByteOrder.Big));
        Assert.Equal(0x04030201u, ByteReading.UInt32(Bytes, ByteOrder.Little));
        Assert.Equal(0x01020304u, ByteReading.UInt32(Bytes, ByteOrder.Big));
        Assert.Equal(0x8007060504030201ul, ByteReading.UInt64(Bytes, ByteOrder.Little));
        Assert.Equal(0x0102030405060780ul, ByteReading.UInt64(Bytes, ByteOrder.Big));
        Assert.Equal(unchecked((long)0x8007060504030201ul), ByteReading.Int64(Bytes, ByteOrder.Little));
        Assert.Equal((short)-1, ByteReading.Int16([0xFF, 0xFF], ByteOrder.Big));
    }

    [Fact]
    public void FloatingPoint_ReadsAtItsOwnPrecision()
    {
        Assert.Equal("1", ByteReading.Float(ByteReading.Single([0x00, 0x00, 0x80, 0x3F], ByteOrder.Little)));
        Assert.Equal("1", ByteReading.Float(ByteReading.Single([0x3F, 0x80, 0x00, 0x00], ByteOrder.Big)));
        Assert.Equal("0.1", ByteReading.Float(ByteReading.Single([0xCD, 0xCC, 0xCC, 0x3D], ByteOrder.Little)));
        Assert.Equal("-2", ByteReading.Float(ByteReading.Double([0, 0, 0, 0, 0, 0, 0, 0xC0], ByteOrder.Little)));
        Assert.Equal("1", ByteReading.Float(ByteReading.Half([0x00, 0x3C], ByteOrder.Little)));
        Assert.Equal("NaN", ByteReading.Float(ByteReading.Single([0xFF, 0xFF, 0xFF, 0xFF], ByteOrder.Little)));
    }

    [Fact]
    public void UnixTimes_ReadAsUtc_AndNothingOutsideTheYearsADateHolds()
    {
        Assert.Equal("2024-01-01 00:00:00", ByteReading.UnixSeconds(1704067200));
        Assert.Equal("1970-01-01 00:00:01.500", ByteReading.UnixMilliseconds(1500));
        Assert.Null(ByteReading.UnixSeconds(long.MaxValue));
        Assert.Null(ByteReading.UnixMilliseconds(long.MinValue));
    }

    [Theory]
    [InlineData(0x00, "NUL")]
    [InlineData(0x0A, "LF")]
    [InlineData(0x20, "SP")]
    [InlineData(0x41, "A")]
    [InlineData(0x7F, "DEL")]
    [InlineData(0x80, null)]
    public void AsciiNames(byte value, string? name) => Assert.Equal(name, ByteReading.AsciiName(value));

    [Fact]
    public void Utf8_ReadsTheCharacterStartingHere_OrNothing()
    {
        Assert.Equal("é U+00E9", ByteReading.Utf8Character([0xC3, 0xA9, 0x41]));
        Assert.Equal("😀 U+1F600", ByteReading.Utf8Character([0xF0, 0x9F, 0x98, 0x80]));
        Assert.Equal("U+000A", ByteReading.Utf8Character([0x0A]));
        Assert.Null(ByteReading.Utf8Character([0xA9]));          // a continuation byte
        Assert.Null(ByteReading.Utf8Character([0xF0, 0x9F]));    // cut short
        Assert.Null(ByteReading.Utf8Character([]));
    }
}
