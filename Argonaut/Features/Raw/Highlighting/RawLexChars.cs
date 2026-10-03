namespace Argonaut.Features.Raw.Highlighting;

/// <summary>Character classes the raw lexers share.</summary>
internal static class RawLexChars
{
    /// <summary>The Control Picture the row decoder draws for a tab.</summary>
    private const char TabPicture = '␉';

    /// <summary>
    /// Whitespace inside a row. A tab can arrive as itself or as its Control Picture, because
    /// the decoder substitutes C0 controls in the text it draws.
    /// </summary>
    public static bool IsBlank(char c) => c == ' ' || c == '\t' || c == TabPicture;

    /// <summary>A character that continues a bare word such as <c>true</c> or <c>null</c>.</summary>
    public static bool IsWordChar(char c) => c == '_' || char.IsLetterOrDigit(c);
}
