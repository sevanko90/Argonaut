using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Argonaut.Features.Hex.Values;

/// <summary>Which end of a multi-byte value comes first in the file.</summary>
public enum ByteOrder
{
    /// <summary>Least significant byte first - x86, ARM as normally run, most file formats.</summary>
    Little,

    /// <summary>Most significant byte first - network order, Java's class files, PNG's lengths.</summary>
    Big,
}

/// <summary>
/// Reads bytes as the values they may hold - integers, floating point, times, characters - in
/// either byte order, and writes those values as text. The Values tab shows every reading at
/// once; the structure decoder reads a field with the same code, so the two never disagree.
/// Each read takes a span at least as long as its type and looks at nothing past it.
/// </summary>
public static class ByteReading
{
    public static ushort UInt16(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    public static short Int16(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes);

    public static uint UInt32(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    public static int Int32(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes);

    public static ulong UInt64(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);

    public static long Int64(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : BinaryPrimitives.ReadInt64BigEndian(bytes);

    public static Half Half(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadHalfLittleEndian(bytes) : BinaryPrimitives.ReadHalfBigEndian(bytes);

    public static float Single(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : BinaryPrimitives.ReadSingleBigEndian(bytes);

    public static double Double(ReadOnlySpan<byte> bytes, ByteOrder order)
        => order == ByteOrder.Little ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes);

    /// <summary>
    /// An unsigned integer of any width from 1 to 8 bytes - a 24-bit length, a 40-bit counter -
    /// read in <paramref name="order"/>.
    /// </summary>
    public static ulong UIntN(ReadOnlySpan<byte> bytes, ByteOrder order)
    {
        ulong value = 0;
        for (int i = 0; i < bytes.Length; i++)
            value = (value << 8) | bytes[order == ByteOrder.Big ? i : bytes.Length - 1 - i];
        return value;
    }

    /// <summary>A two's-complement integer of any width from 1 to 8 bytes, its top bit the sign.</summary>
    public static long IntN(ReadOnlySpan<byte> bytes, ByteOrder order)
    {
        int unused = 64 - bytes.Length * 8;
        return (long)(UIntN(bytes, order) << unused) >> unused;
    }

    /// <summary>The bytes as UTF-8 text, or null when they are not whole, valid UTF-8 - a
    /// character cut off at either end included.</summary>
    public static string? Utf8Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || !System.Text.Unicode.Utf8.IsValid(bytes))
            return null;

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>An integer as plain digits - what a copy puts on the clipboard.</summary>
    public static string Integer<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>A floating-point value at the shortest text that reads back as the same value.</summary>
    public static string Float(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A floating-point value at its own precision - a float as a float, not as the
    /// longer double it widens to.</summary>
    public static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Float(Half value) => value.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>Seconds since 1970-01-01 UTC as a date and time, or null outside the years a
    /// date can hold.</summary>
    public static string? UnixSeconds(long seconds)
    {
        const long min = -62135596800;   // 0001-01-01
        const long max = 253402300799;   // 9999-12-31 23:59:59
        return seconds is < min or > max ? null : DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Milliseconds since 1970-01-01 UTC as a date and time, or null outside the years a
    /// date can hold.</summary>
    public static string? UnixMilliseconds(long milliseconds)
    {
        const long min = -62135596800000;
        const long max = 253402300799999;
        return milliseconds is < min or > max ? null : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static readonly string[] ControlNames =
    [
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US",
    ];

    /// <summary>What a byte is in ASCII: a control's abbreviation (<c>NUL</c>, <c>LF</c>), <c>SP</c>
    /// for a space, the character itself, or null above 0x7F, which ASCII does not name.</summary>
    public static string? AsciiName(byte value) => value switch
    {
        < 0x20 => ControlNames[value],
        0x20 => "SP",
        0x7F => "DEL",
        < 0x80 => ((char)value).ToString(),
        _ => null,
    };

    /// <summary>
    /// The UTF-8 character starting at the first byte, as the character and its code point
    /// (<c>é U+00E9</c>), or null when no character starts there - a continuation byte, an
    /// invalid sequence, or one cut short by the end of <paramref name="bytes"/>.
    /// </summary>
    public static string? Utf8Character(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || Rune.DecodeFromUtf8(bytes, out var rune, out _) != System.Buffers.OperationStatus.Done)
            return null;

        string shown = Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) ? string.Empty : rune.ToString() + " ";
        return $"{shown}U+{rune.Value:X4}";
    }
}
