using System;
using System.Collections.Generic;
using System.Globalization;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// Evaluates the integer expressions a definition sizes its arrays with: literals in decimal, hex,
/// binary and (in C) octal, with C's suffixes; named constants; and <c>+ - * / % &lt;&lt; &gt;&gt;</c>
/// with parentheses. Anything else - <c>sizeof</c>, a cast, an unknown name - is not a constant.
/// </summary>
internal sealed class ConstantExpression
{
    private readonly IReadOnlyList<Lexeme> lexemes;
    private readonly int end;
    private readonly IReadOnlyDictionary<string, long> constants;
    private readonly bool octalLiterals;
    private int index;

    private ConstantExpression(IReadOnlyList<Lexeme> lexemes, int start, int end, IReadOnlyDictionary<string, long> constants, bool octalLiterals)
    {
        this.lexemes = lexemes;
        this.index = start;
        this.end = end;
        this.constants = constants;
        this.octalLiterals = octalLiterals;
    }

    /// <summary>Evaluates <paramref name="lexemes"/> from <paramref name="start"/> up to, not
    /// including, <paramref name="end"/>, all of which must be the expression.</summary>
    public static bool TryEvaluate(
        IReadOnlyList<Lexeme> lexemes,
        int start,
        int end,
        IReadOnlyDictionary<string, long> constants,
        bool octalLiterals,
        out long result)
    {
        var expression = new ConstantExpression(lexemes, start, end, constants, octalLiterals);
        try
        {
            if (start < end && expression.TryShift(out result) && expression.index == end)
                return true;
        }
        catch (OverflowException)
        {
        }
        catch (DivideByZeroException)
        {
        }

        result = 0;
        return false;
    }

    /// <summary>Reads an integer literal as written in either language.</summary>
    public static bool TryParseLiteral(string text, bool octalLiterals, out long result)
    {
        string digits = text.Replace("'", string.Empty).Replace("_", string.Empty).TrimEnd('u', 'U', 'l', 'L');
        result = 0;
        if (digits.Length == 0)
            return false;

        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(digits.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);

        if (digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return TryParseBase(digits.AsSpan(2), 2, out result);

        if (octalLiterals && digits.Length > 1 && digits[0] == '0')
            return TryParseBase(digits.AsSpan(1), 8, out result);

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseBase(ReadOnlySpan<char> digits, int radix, out long result)
    {
        result = 0;
        if (digits.IsEmpty)
            return false;

        foreach (char c in digits)
        {
            int digit = c - '0';
            if (digit < 0 || digit >= radix)
                return false;
            result = checked(result * radix + digit);
        }

        return true;
    }

    private bool At(string symbol) => this.index < this.end && this.lexemes[this.index].Is(symbol);

    private bool TryShift(out long value)
    {
        if (!TryAdditive(out value))
            return false;

        while (At("<<") || At(">>"))
        {
            bool left = this.lexemes[this.index++].Text == "<<";
            if (!TryAdditive(out long by) || by is < 0 or > 62)
                return false;
            value = left ? checked(value << (int)by) : value >> (int)by;
        }

        return true;
    }

    private bool TryAdditive(out long value)
    {
        if (!TryMultiplicative(out value))
            return false;

        while (At("+") || At("-"))
        {
            bool plus = this.lexemes[this.index++].Text == "+";
            if (!TryMultiplicative(out long right))
                return false;
            value = plus ? checked(value + right) : checked(value - right);
        }

        return true;
    }

    private bool TryMultiplicative(out long value)
    {
        if (!TryUnary(out value))
            return false;

        while (At("*") || At("/") || At("%"))
        {
            string op = this.lexemes[this.index++].Text;
            if (!TryUnary(out long right))
                return false;
            value = op switch
            {
                "*" => checked(value * right),
                "/" => value / right,
                _ => value % right,
            };
        }

        return true;
    }

    private bool TryUnary(out long value)
    {
        if (At("-"))
        {
            this.index++;
            bool read = TryUnary(out value);
            value = checked(-value);
            return read;
        }

        if (At("+"))
        {
            this.index++;
            return TryUnary(out value);
        }

        return TryPrimary(out value);
    }

    private bool TryPrimary(out long value)
    {
        value = 0;
        if (this.index >= this.end)
            return false;

        var lexeme = this.lexemes[this.index];
        if (lexeme.Is("("))
        {
            this.index++;
            if (!TryShift(out value) || !At(")"))
                return false;
            this.index++;
            return true;
        }

        this.index++;
        return lexeme.Kind switch
        {
            LexemeKind.Number => TryParseLiteral(lexeme.Text, this.octalLiterals, out value),
            LexemeKind.Identifier => this.constants.TryGetValue(lexeme.Text, out value),
            _ => false,
        };
    }
}
