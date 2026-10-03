namespace Argonaut.Features.Raw.Highlighting;

/// <summary>
/// Lexer state carried from one row to the next row of the SAME line. Zero is "at a line start".
/// Each lexer packs its own bits; <see cref="Unknown"/> means "cannot tell" and makes the rest of
/// the line plain.
/// </summary>
public readonly record struct RawLexState(int Bits)
{
    public static RawLexState LineStart => new(0);

    public static RawLexState Unknown => new(-1);

    public bool IsUnknown => Bits < 0;
}
