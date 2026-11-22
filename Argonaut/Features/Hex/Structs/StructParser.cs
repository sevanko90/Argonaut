using System.Text.RegularExpressions;

namespace Argonaut.Features.Hex.Structs;

/// <summary>Reads a pasted struct definition in C/C++ or C#, telling which unless told.</summary>
public static partial class StructParser
{
    public static StructDefinition Parse(string source, StructLanguage? language = null)
    {
        var chosen = language ?? Detect(source);
        StructSourceParser parser = chosen == StructLanguage.CSharp ? new CSharpStructParser(source) : new CStructParser(source);
        return parser.Parse();
    }

    /// <summary>
    /// Which language <paramref name="source"/> reads as, from the marks only one of them makes:
    /// attributes, access modifiers on members, <c>fixed</c> and C#'s own type names on one side;
    /// the preprocessor, <c>typedef</c>, <c>};</c>, access labels and C's type names on the other.
    /// A tie reads as C, the commoner thing to paste.
    /// </summary>
    public static StructLanguage Detect(string source)
    {
        int csharp = CSharpMarks().Count(source);
        int c = CMarks().Count(source);
        return csharp > c ? StructLanguage.CSharp : StructLanguage.C;
    }

    [GeneratedRegex(@"\[\s*(StructLayout|MarshalAs|FieldOffset)|\busing\s+System\b|\b(public|private|internal)\s+(?!:)\w|\bfixed\s+\w+\s+\w+\s*\[|\b(byte|sbyte|ushort|uint|ulong|nint|nuint|string)\s+\w+\s*[;={]|\breadonly\s+struct\b|\brecord\s+struct\b|\{\s*get\s*;")]
    private static partial Regex CSharpMarks();

    [GeneratedRegex(@"^\s*#\s*\w+|\btypedef\b|\}\s*;|\b(public|private|protected)\s*:|\bunsigned\s+\w+|\b(u?int(8|16|32|64)_t|size_t|wchar_t|char16_t|char32_t)\b|\bstruct\s+\w+\s+\w+\s*;|\w\s*\*\s*\w+\s*;", RegexOptions.Multiline)]
    private static partial Regex CMarks();
}
