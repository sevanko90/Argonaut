using System.Text;
using Argonaut.Features.Hex.Values;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// How each type is stored in the language and target in use, said in a sentence - the struct
/// tree's hover on a row. Whether a C# <c>bool</c> is a bit or an <c>int</c>, how long a C
/// <c>long</c> is, why a C# <c>char[256]</c> is 512 bytes: the questions a layout raises are
/// answered where the row shows them, never left to be inferred.
/// </summary>
public static class StructStorageNotes
{
    /// <summary>
    /// The note for <paramref name="node"/>: its type and how its bytes are stored, then where it
    /// sits - <c>bool (C#): 1 byte, 0 = false, otherwise true …</c>.
    /// </summary>
    public static string For(in StructNode node, StructLanguage language, StructTarget target, ByteOrder order)
    {
        if (node.Kind == StructNodeKind.Padding)
            return $"Padding: {Bytes(node.Size)} the layout adds so the next field, or the next struct in an array, starts at its alignment. Never read.";

        var value = node.Value;
        var note = new StringBuilder();
        if (value.IsArray)
        {
            var element = value.ElementValue;
            note.Append(value.TypeText()).Append(": ").Append(value.Count).Append(" × ").Append(Bytes(value.Stride))
                .Append(" = ").Append(Bytes(value.Size)).Append(", element i at +i × ").Append(value.Stride).Append('.');
            if (!element.IsArray && element.Scalar is { Kind: StructScalarKind.Character })
                note.Append(" Shown as text up to the first NUL.");
            else if (StructValueText.CanReadAsText(node))
                note.Append(" Shown as ASCII text up to the first NUL when its bytes are printable that far - C# code often holds a string in bytes - else as its items.");
            note.AppendLine().AppendLine();
            if (element.IsArray)
                return note.Append(At(node)).ToString();
            value = element;
        }

        if (value.Struct is { } laid)
        {
            note.Append(laid.Kind == StructKind.Union ? "union " : "struct ").Append(laid.Name).Append(": ").Append(Bytes(laid.Size))
                .Append(", align ").Append(laid.Alignment).Append('.');
            if (laid.Size > laid.DataSize)
                note.Append(' ').Append(Bytes(laid.Size - laid.DataSize)).Append(" of it trailing padding, so the next one in an array is aligned.");
        }
        else if (value.Scalar is { } scalar)
        {
            note.Append(Scalar(value, scalar, language, target));
            if (scalar.Size > 1 && value.BitWidth is null && scalar.Kind != StructScalarKind.Bool)
                note.Append(" Read ").Append(order == ByteOrder.Little ? "little-endian" : "big-endian").Append(", the Values tab's byte order.");
        }

        return note.AppendLine().AppendLine().Append(At(node)).ToString();
    }

    private static string Scalar(in StructValue value, StructScalar scalar, StructLanguage language, StructTarget target)
    {
        string named = $"{value.TypeName} ({Language(language, target, scalar)})";

        if (value.BitWidth is { } width)
            return $"{value.TypeName} : {width} (C): {width} bit{(width == 1 ? string.Empty : "s")} of a {scalar.Size}-byte storage unit, "
                + $"starting at bit {value.BitOffset} - consecutive bitfields share the unit, lowest bits first"
                + (scalar.Kind == StructScalarKind.Signed ? ", sign-extended." : ".");

        if (value.Enum is { } enumeration)
            return $"{enumeration.Name} ({LanguageName(language)}): an enum stored as its underlying {scalar.Name}, {Bytes(scalar.Size)}, "
                + (scalar.Kind == StructScalarKind.Signed ? "signed" : "unsigned") + "; shown by name and number"
                + (enumeration.IsFlags ? ", names combined for [Flags]." : ".");

        return scalar.Kind switch
        {
            StructScalarKind.Bool when language == StructLanguage.CSharp && scalar.Size == 4
                => $"{named}: 4 bytes, marshalled as Win32 BOOL by [MarshalAs(UnmanagedType.Bool)]; 0 = false, otherwise true. Unmarshalled, a C# bool is 1 byte.",
            StructScalarKind.Bool when language == StructLanguage.CSharp
                => $"{named}: 1 byte, 0 = false, otherwise true - not a bit, and not the 4 bytes of [MarshalAs(UnmanagedType.Bool)].",
            StructScalarKind.Bool
                => $"{named}: 1 byte, 0 = false, 1 = true. Only a bitfield (bool flag : 1) takes a single bit.",
            StructScalarKind.Character when value.TypeName == "string"
                => $"string (C#): marshalled in place by [MarshalAs(UnmanagedType.ByValTStr)], {Bytes(scalar.Size)} a character"
                    + (scalar.Size == 1 ? " (CharSet.Unicode makes it 2)." : " (CharSet.Unicode)."),
            StructScalarKind.Character when language == StructLanguage.CSharp
                => $"{named}: 2 bytes, a UTF-16 code unit - so char[256] is 512 bytes where C's is 256. Use byte for a 1-byte character.",
            StructScalarKind.Character when scalar.Size == 1
                => $"{named}: 1 byte, {(target.CharIsSigned ? "signed" : "unsigned")} on this target.",
            StructScalarKind.Character when value.TypeName == "wchar_t"
                => $"{named}: {Bytes(scalar.Size)}, {(scalar.Size == 2 ? "UTF-16" : "UTF-32")} - 2 bytes on Windows (LLP64), 4 on macOS and Linux (LP64).",
            StructScalarKind.Character
                => $"{named}: {Bytes(scalar.Size)}, a {(scalar.Size == 2 ? "UTF-16" : "UTF-32")} code unit.",
            StructScalarKind.Pointer
                => $"{named}: a {scalar.Size}-byte address, shown and never followed.",
            StructScalarKind.Float
                => $"{named}: {Bytes(scalar.Size)}, IEEE 754 {(scalar.Size switch { 2 => "half", 4 => "single", _ => "double" })} precision.",
            _ when language == StructLanguage.C && value.TypeName is "long" or "unsigned long"
                => $"{named}: {Bytes(scalar.Size)}, {Sign(scalar)} - 4 bytes on Windows (LLP64), 8 on macOS and Linux (LP64).",
            _ when language == StructLanguage.C && value.TypeName is "size_t" or "uintptr_t" or "ssize_t" or "intptr_t" or "ptrdiff_t"
                => $"{named}: {Bytes(scalar.Size)}, {Sign(scalar)} - a pointer's width on this target.",
            _ when language == StructLanguage.CSharp && value.TypeName is "nint" or "nuint" or "IntPtr" or "UIntPtr"
                => $"{named}: {Bytes(scalar.Size)}, {Sign(scalar)} - a pointer's width.",
            _ => $"{named}: {Bytes(scalar.Size)}, {Sign(scalar)}.",
        };
    }

    private static string Sign(StructScalar scalar) => scalar.Kind == StructScalarKind.Signed ? "signed, two's complement" : "unsigned";

    /// <summary>The language, and for C the target when the type depends on it.</summary>
    private static string Language(StructLanguage language, StructTarget target, StructScalar scalar)
        => language == StructLanguage.CSharp ? "C#" : DependsOnTarget(scalar) ? $"C, {ShortName(target)}" : "C";

    private static bool DependsOnTarget(StructScalar scalar)
        => scalar.Kind == StructScalarKind.Pointer || scalar.Name is "long" or "unsigned long" or "wchar_t" or "char" or "size_t" or "uintptr_t" or "ssize_t" or "intptr_t" or "ptrdiff_t";

    private static string ShortName(StructTarget target) => target.Name.Split(' ')[0];

    private static string LanguageName(StructLanguage language) => language == StructLanguage.CSharp ? "C#" : "C";

    private static string At(in StructNode node)
    {
        string bits = node.Value.BitWidth is not null && node.Kind == StructNodeKind.Field ? $", bit {node.Value.BitOffset}" : string.Empty;
        return $"{Bytes(node.Size)} at +{node.Offset}{bits}";
    }

    private static string Bytes(long count) => count == 1 ? "1 byte" : $"{count} bytes";
}
