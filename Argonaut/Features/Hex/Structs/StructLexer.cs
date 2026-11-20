using System;
using System.Collections.Generic;
using System.Text;

namespace Argonaut.Features.Hex.Structs;

internal enum LexemeKind
{
    Identifier,
    Number,
    Symbol,

    /// <summary>A string or character literal - kept only so it is never read as code.</summary>
    Quoted,

    /// <summary>A whole preprocessor line, without its <c>#</c>.</summary>
    Directive,
    End,
}

internal readonly record struct Lexeme(LexemeKind Kind, string Text, SourcePosition Position)
{
    public bool Is(string text) => Kind is LexemeKind.Symbol or LexemeKind.Identifier && Text == text;
}

/// <summary>
/// Splits a definition into identifiers, numbers and symbols for the C and C# parsers, dropping
/// comments and whitespace. A preprocessor line comes through whole, continuation lines joined, so
/// the C parser can read <c>#define</c> and ignore the rest.
/// </summary>
internal static class StructLexer
{
    private static readonly string[] TwoCharacterSymbols = ["::", "<<", ">>", "=>", "->", "=="];

    public static List<Lexeme> Read(string source)
    {
        var lexemes = new List<Lexeme>();
        int i = 0, line = 1, lineStart = 0;
        bool atLineStart = true;

        char At(int index) => index < source.Length ? source[index] : '\0';

        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                i++;
                line++;
                lineStart = i;
                atLineStart = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var position = new SourcePosition(line, i - lineStart + 1);
            if (c == '/' && At(i + 1) == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && At(i + 1) == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && At(i + 1) == '/'))
                {
                    if (source[i] == '\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }

                    i++;
                }

                i = Math.Min(source.Length, i + 2);
                continue;
            }

            if (c == '#' && atLineStart)
            {
                var directive = new StringBuilder();
                i++;
                while (i < source.Length && source[i] != '\n')
                {
                    if (source[i] == '\\' && At(i + 1) == '\n')
                    {
                        i += 2;
                        line++;
                        lineStart = i;
                        directive.Append(' ');
                        continue;
                    }

                    if (source[i] == '/' && At(i + 1) == '/')
                    {
                        while (i < source.Length && source[i] != '\n')
                            i++;
                        break;
                    }

                    directive.Append(source[i]);
                    i++;
                }

                lexemes.Add(new(LexemeKind.Directive, directive.ToString().Trim(), position));
                continue;
            }

            atLineStart = false;
            int start = i;
            if (char.IsLetter(c) || c == '_')
            {
                while (char.IsLetterOrDigit(At(i)) || At(i) == '_')
                    i++;
                lexemes.Add(new(LexemeKind.Identifier, source[start..i], position));
            }
            else if (char.IsDigit(c))
            {
                while (char.IsLetterOrDigit(At(i)) || At(i) is '_' or '\'' or '.')
                    i++;
                lexemes.Add(new(LexemeKind.Number, source[start..i], position));
            }
            else if (c is '"' or '\'' || (c == '@' && At(i + 1) == '"'))
            {
                bool verbatim = c == '@';
                if (verbatim)
                    i++;
                char quote = source[i];
                i++;
                while (i < source.Length && source[i] != quote && source[i] != '\n')
                {
                    if (source[i] == '\\' && !verbatim)
                        i++;
                    i++;
                }

                i = Math.Min(source.Length, i + 1);
                lexemes.Add(new(LexemeKind.Quoted, source[start..i], position));
            }
            else
            {
                string pair = i + 1 < source.Length ? source.Substring(i, 2) : string.Empty;
                if (Array.IndexOf(TwoCharacterSymbols, pair) >= 0)
                {
                    lexemes.Add(new(LexemeKind.Symbol, pair, position));
                    i += 2;
                }
                else
                {
                    lexemes.Add(new(LexemeKind.Symbol, c.ToString(), position));
                    i++;
                }
            }
        }

        lexemes.Add(new(LexemeKind.End, string.Empty, new SourcePosition(line, i - lineStart + 1)));
        return lexemes;
    }
}
