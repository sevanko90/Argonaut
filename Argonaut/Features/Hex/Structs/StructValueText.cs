using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex.Values;

namespace Argonaut.Features.Hex.Structs;

/// <summary>What a decoded value is, for colouring.</summary>
public enum StructTextKind : byte
{
    Number,
    Text,

    /// <summary><c>true</c>, <c>false</c>, an enum's name.</summary>
    Keyword,

    /// <summary>What a collapsed struct or array holds.</summary>
    Summary,

    /// <summary>Bytes past the end of the document: <c>-</c>.</summary>
    Missing,
}

/// <summary>A value as shown.</summary>
public readonly record struct StructText(string Text, StructTextKind Kind);

/// <summary>
/// Reads a node's bytes as its type: integers, floats, bools, characters, pointers, enums by name,
/// bitfields by their bits, and arrays of characters or bytes asked to read as text as the string
/// up to their first NUL. Reads with
/// <see cref="ByteReading"/>, as the Values tab does, so a field and the tab never disagree; each
/// read copies at most a bounded number of bytes into a stack buffer.
/// </summary>
public static class StructValueText
{
    /// <summary>How many characters of a character array are shown.</summary>
    public const int MaximumStringLength = 256;

    private const int MaximumStringBytes = MaximumStringLength * 4;

    /// <param name="bytes">The document.</param>
    /// <param name="start">Where the mapping starts in the document.</param>
    /// <param name="asText">Whether an array that <see cref="CanReadAsText"/> reads as text rather
    /// than as its count of items.</param>
    public static StructText Read(IByteSource bytes, long start, in StructNode node, ByteOrder order, StructTarget target, bool asText)
    {
        var value = node.Value;
        long at = start + node.Offset;
        if (node.Kind == StructNodeKind.Padding)
            return new StructText($"{node.Size} byte{(node.Size == 1 ? string.Empty : "s")}", StructTextKind.Summary);

        if (asText && CanReadAsText(node))
        {
            var element = value.Scalar!;
            return ReadString(bytes, at, value.Count, element.Size, element.Kind == StructScalarKind.Character ? null : Encoding.Latin1);
        }

        if (value.IsArray)
            return new StructText($"{value.Count} item{(value.Count == 1 ? string.Empty : "s")}", StructTextKind.Summary);

        if (value.Struct is { } laid)
        {
            int fields = 0;
            foreach (var field in laid.Fields)
                fields += field.IsBase ? 0 : 1;
            return new StructText($"{fields} field{(fields == 1 ? string.Empty : "s")}", StructTextKind.Summary);
        }

        var scalar = value.Scalar!;
        int size = value.BitWidth is { } bits ? (value.BitOffset + bits + 7) / 8 : scalar.Size;
        if (at < 0 || at + size > bytes.AvailableLength)
            return new StructText("-", StructTextKind.Missing);

        Span<byte> buffer = stackalloc byte[8];
        var read = buffer[..size];
        if (bytes.CopyTo(at, read) < size)
            return new StructText("-", StructTextKind.Missing);

        if (value.BitWidth is { } width)
            return Bitfield(read, value, scalar, width, order, target);

        return Scalar(read, scalar, value.Enum, order, target);
    }

    /// <summary>
    /// Whether <paramref name="node"/> is an array that can be read as text: the innermost
    /// dimension of an array of characters, or of 1-byte integers - which C# code often uses for a
    /// string, having no 1-byte character.
    /// </summary>
    public static bool CanReadAsText(in StructNode node)
    {
        var value = node.Value;
        return node.Kind != StructNodeKind.Padding && value.IsArray && value.Dimension == value.Lengths.Count - 1 && value.Struct is null && value.Enum is null
            && value.Scalar is { Kind: StructScalarKind.Character } or { Kind: StructScalarKind.Signed or StructScalarKind.Unsigned, Size: 1 };
    }

    /// <summary>
    /// Whether an array that can be read as text should be by default: a character array always;
    /// a byte array when its bytes look like text - printable ASCII up to a NUL, or printable all
    /// the way through. Looks at no more than <see cref="MaximumStringBytes"/> bytes.
    /// </summary>
    public static bool ReadsAsTextByDefault(IByteSource bytes, long start, in StructNode node)
    {
        if (!CanReadAsText(node))
            return false;
        if (node.Value.Scalar!.Kind == StructScalarKind.Character)
            return true;

        long at = start + node.Offset;
        long wanted = Math.Min(node.Value.Count, MaximumStringBytes);
        long available = Math.Max(0, Math.Min(wanted, bytes.AvailableLength - at));
        if (at < 0 || available == 0)
            return false;

        Span<byte> buffer = stackalloc byte[MaximumStringBytes];
        var read = buffer[..bytes.CopyTo(at, buffer[..(int)available])];
        int printable = 0;
        foreach (byte b in read)
        {
            if (b == 0)
                return printable > 0;
            if (b is not (>= 0x20 and <= 0x7E or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                return false;
            printable++;
        }

        // No NUL in what was read: text if it was all of the array, or a run too long to be chance.
        return printable > 0 && (read.Length == node.Value.Count || read.Length == MaximumStringBytes);
    }

    private static StructText Scalar(ReadOnlySpan<byte> read, StructScalar scalar, EnumDeclaration? enumeration, ByteOrder order, StructTarget target)
    {
        switch (scalar.Kind)
        {
            case StructScalarKind.Float:
                return new StructText(scalar.Size switch
                {
                    2 => ByteReading.Float(ByteReading.Half(read, order)),
                    4 => ByteReading.Float(ByteReading.Single(read, order)),
                    _ => ByteReading.Float(ByteReading.Double(read, order)),
                }, StructTextKind.Number);

            case StructScalarKind.Pointer:
                return new StructText("0x" + ByteReading.UIntN(read, order).ToString($"X{scalar.Size * 2}", CultureInfo.InvariantCulture), StructTextKind.Number);

            case StructScalarKind.Bool:
                return Bool(ByteReading.UIntN(read, order));

            case StructScalarKind.Character:
                return Character(read, scalar, order, target);

            case StructScalarKind.Signed:
                return Integer(ByteReading.IntN(read, order), enumeration);

            default:
                ulong unsigned = ByteReading.UIntN(read, order);
                return enumeration is null || unsigned > long.MaxValue
                    ? new StructText(ByteReading.Integer(unsigned), StructTextKind.Number)
                    : Integer((long)unsigned, enumeration);
        }
    }

    /// <summary>
    /// A bitfield's bits. Little-endian, bits are allocated from the lowest of the storage read
    /// least-significant-byte-first; big-endian, from the highest of the storage read
    /// most-significant-byte-first - as compilers for each lay them out.
    /// </summary>
    private static StructText Bitfield(ReadOnlySpan<byte> read, in StructValue value, StructScalar scalar, int width, ByteOrder order, StructTarget target)
    {
        ulong raw = ByteReading.UIntN(read, order);
        int shift = order == ByteOrder.Little ? value.BitOffset : read.Length * 8 - value.BitOffset - width;
        ulong mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong bits = (raw >> shift) & mask;

        bool signed = scalar.Kind == StructScalarKind.Signed || (scalar.Kind == StructScalarKind.Character && target.CharIsSigned);
        if (scalar.Kind == StructScalarKind.Bool)
            return Bool(bits);

        if (signed && width < 64 && (bits & (1UL << (width - 1))) != 0)
            return Integer((long)(bits | ~mask), value.Enum);

        return bits > long.MaxValue ? new StructText(ByteReading.Integer(bits), StructTextKind.Number) : Integer((long)bits, value.Enum);
    }

    private static StructText Bool(ulong raw) => raw switch
    {
        0 => new StructText("false", StructTextKind.Keyword),
        1 => new StructText("true", StructTextKind.Keyword),
        _ => new StructText($"true ({raw})", StructTextKind.Keyword),
    };

    /// <summary>An integer, by its enum's name when it has one: <c>Read (1)</c>, flags combined as
    /// <c>Read | Write (3)</c>, or the number alone when no name fits.</summary>
    private static StructText Integer(long number, EnumDeclaration? enumeration)
    {
        string digits = ByteReading.Integer(number);
        if (enumeration is null)
            return new StructText(digits, StructTextKind.Number);

        foreach (var member in enumeration.Members)
        {
            if (member.Value == number)
                return new StructText($"{member.Name} ({digits})", StructTextKind.Keyword);
        }

        if (IsFlags(enumeration) && number > 0)
        {
            var names = new StringBuilder();
            long remaining = number;
            foreach (var member in enumeration.Members)
            {
                if (member.Value > 0 && BitOperations.IsPow2(member.Value) && (remaining & member.Value) == member.Value)
                {
                    names.Append(names.Length > 0 ? " | " : string.Empty).Append(member.Name);
                    remaining &= ~member.Value;
                }
            }

            if (remaining == 0)
                return new StructText($"{names} ({digits})", StructTextKind.Keyword);
        }

        return new StructText(digits, StructTextKind.Number);
    }

    /// <summary>A C# <c>[Flags]</c> enum, or a C one whose every non-zero value is a power of two.</summary>
    private static bool IsFlags(EnumDeclaration enumeration)
    {
        if (enumeration.IsFlags)
            return true;

        int powers = 0;
        foreach (var member in enumeration.Members)
        {
            if (member.Value == 0)
                continue;
            if (member.Value < 0 || !BitOperations.IsPow2(member.Value))
                return false;
            powers++;
        }

        return powers >= 2;
    }

    private static StructText Character(ReadOnlySpan<byte> read, StructScalar scalar, ByteOrder order, StructTarget target)
    {
        ulong code = ByteReading.UIntN(read, order);
        string number = scalar.Size == 1 && target.CharIsSigned ? ByteReading.Integer((sbyte)code) : ByteReading.Integer(code);
        string? shown = scalar.Size == 1
            ? ByteReading.AsciiName((byte)code) is { } ascii && code is > 0x20 and < 0x7F ? ascii : null
            : code is > 0x20 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32((int)code) : null;
        return new StructText(shown is null ? number : $"'{shown}' ({number})", StructTextKind.Text);
    }

    /// <summary>An array as text up to its first NUL, decoded by its width - UTF-8, UTF-16 or
    /// UTF-32, or <paramref name="bytesAs"/> for bytes that are not characters - and capped at
    /// <see cref="MaximumStringLength"/> characters.</summary>
    private static StructText ReadString(IByteSource bytes, long at, long count, int width, Encoding? bytesAs)
    {
        long wanted = Math.Min(count * width, MaximumStringBytes - MaximumStringBytes % width);
        long available = Math.Max(0, Math.Min(wanted, bytes.AvailableLength - at));
        if (available < width)
            return new StructText("-", StructTextKind.Missing);

        Span<byte> buffer = stackalloc byte[MaximumStringBytes];
        var read = buffer[..bytes.CopyTo(at, buffer[..(int)(available - available % width)])];

        int end = 0;
        while (end + width <= read.Length && !IsNul(read.Slice(end, width)))
            end += width;

        var encoding = bytesAs ?? width switch
        {
            1 => (Encoding)new UTF8Encoding(false, false),
            2 => Encoding.Unicode,
            _ => Encoding.UTF32,
        };
        string text = encoding.GetString(read[..end]);
        if (text.Length > MaximumStringLength)
            text = text[..MaximumStringLength];
        bool cut = end == read.Length && read.Length < count * width;
        return new StructText(Quote(text) + (cut ? "…" : string.Empty), StructTextKind.Text);
    }

    private static bool IsNul(ReadOnlySpan<byte> character)
    {
        foreach (byte b in character)
        {
            if (b != 0)
                return false;
        }

        return true;
    }

    private static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (char c in text)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }
}
