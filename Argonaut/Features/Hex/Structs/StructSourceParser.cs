using System;
using System.Collections.Generic;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// What the C and C# parsers share: walking the lexemes, skipping what is not a declaration, and
/// collecting the structs, aliases, constants and problems. Each language reads its own
/// declarations; anything it does not understand is skipped a statement at a time, so one
/// unreadable member never loses the rest.
/// </summary>
internal abstract class StructSourceParser
{
    private readonly List<Lexeme> lexemes;
    private int index;

    protected StructSourceParser(string source)
    {
        this.lexemes = StructLexer.Read(source);
    }

    protected List<StructDeclaration> Declarations { get; } = [];

    protected List<EnumDeclaration> Enums { get; } = [];

    protected Dictionary<string, TypeReference> Aliases { get; } = new(StringComparer.Ordinal);

    protected Dictionary<string, long> Constants { get; } = new(StringComparer.Ordinal);

    protected List<StructProblem> Problems { get; } = [];

    protected abstract StructLanguage Language { get; }

    protected Lexeme Current => this.lexemes[this.index];

    protected bool AtEnd => Current.Kind == LexemeKind.End;

    protected Lexeme PeekAhead(int offset) => this.lexemes[Math.Min(this.index + offset, this.lexemes.Count - 1)];

    protected Lexeme Advance()
    {
        var lexeme = Current;
        if (!AtEnd)
            this.index++;
        return lexeme;
    }

    protected bool Accept(string text)
    {
        if (!Current.Is(text))
            return false;
        this.index++;
        return true;
    }

    protected bool AtIdentifier => Current.Kind == LexemeKind.Identifier;

    protected int Mark => this.index;

    protected void Rewind(int mark) => this.index = mark;

    protected void Report(string message, SourcePosition position) => Problems.Add(new StructProblem(message, position));

    public StructDefinition Parse()
    {
        ParseDeclarations(insideBraces: false);
        return new StructDefinition(Language, Declarations, Enums, Aliases, Problems);
    }

    /// <summary>Reads declarations up to the end, or to a closing brace when inside a namespace's.</summary>
    protected abstract void ParseDeclarations(bool insideBraces);

    /// <summary>
    /// Skips one statement: up to and including the next <c>;</c> outside brackets, or a whole
    /// braced block (a method's body, a type that is not read) and an initializer or <c>;</c> that
    /// follows it. Stops before a <c>}</c> that closes the enclosing block.
    /// </summary>
    protected void SkipStatement()
    {
        while (!AtEnd)
        {
            if (Current.Is(";"))
            {
                Advance();
                return;
            }

            if (Current.Is("}"))
                return;

            if (Current.Is("{"))
            {
                SkipBalanced();
                if (Current.Is("="))
                    continue;
                Accept(";");
                return;
            }

            if (Current.Is("(") || Current.Is("["))
            {
                SkipBalanced();
                continue;
            }

            Advance();
        }
    }

    /// <summary>Skips from an opening bracket past the one that closes it.</summary>
    protected void SkipBalanced()
    {
        int depth = 0;
        while (!AtEnd)
        {
            var lexeme = Advance();
            if (lexeme.Is("(") || lexeme.Is("[") || lexeme.Is("{"))
                depth++;
            else if (lexeme.Is(")") || lexeme.Is("]") || lexeme.Is("}"))
            {
                depth--;
                if (depth <= 0)
                    return;
            }
        }
    }

    /// <summary>Skips an initializer up to the <c>,</c> or <c>;</c> that ends it, leaving that.</summary>
    protected void SkipInitializer()
    {
        while (!AtEnd && !Current.Is(",") && !Current.Is(";") && !Current.Is("}"))
        {
            if (Current.Is("(") || Current.Is("[") || Current.Is("{"))
                SkipBalanced();
            else
                Advance();
        }
    }

    /// <summary>Where the bracket opened at <paramref name="open"/> closes, or -1.</summary>
    protected int FindClose(int open)
    {
        int depth = 0;
        for (int i = open; i < this.lexemes.Count; i++)
        {
            var lexeme = this.lexemes[i];
            if (lexeme.Is("(") || lexeme.Is("[") || lexeme.Is("{"))
                depth++;
            else if (lexeme.Is(")") || lexeme.Is("]") || lexeme.Is("}"))
            {
                depth--;
                if (depth == 0)
                    return i;
            }
            else if (lexeme.Kind == LexemeKind.End)
                break;
        }

        return -1;
    }

    /// <summary>Evaluates the expression from here up to, not including, <paramref name="end"/>,
    /// leaving the position at <paramref name="end"/> either way.</summary>
    protected bool TryEvaluateTo(int end, out long value) => TryEvaluateTo(end, Constants, out value);

    private bool TryEvaluateTo(int end, IReadOnlyDictionary<string, long> constants, out long value)
    {
        bool read = ConstantExpression.TryEvaluate(this.lexemes, this.index, end, constants, Language == StructLanguage.C, out value);
        this.index = end;
        return read;
    }

    /// <summary>Evaluates the expression up to the next <c>,</c>, <c>;</c> or <c>)</c> outside
    /// brackets, leaving the position there.</summary>
    protected bool TryEvaluateExpression(out long value) => TryEvaluateExpression(Constants, out value);

    /// <summary>As <see cref="TryEvaluateExpression(out long)"/>, naming
    /// <paramref name="constants"/> rather than the definition's.</summary>
    protected bool TryEvaluateExpression(IReadOnlyDictionary<string, long> constants, out long value)
    {
        int end = this.index;
        int depth = 0;
        while (end < this.lexemes.Count - 1)
        {
            var lexeme = this.lexemes[end];
            if (depth == 0 && (lexeme.Is(",") || lexeme.Is(";") || lexeme.Is(")") || lexeme.Is("}")))
                break;
            if (lexeme.Is("(") || lexeme.Is("["))
                depth++;
            else if (lexeme.Is(")") || lexeme.Is("]"))
                depth--;
            end++;
        }

        return TryEvaluateTo(end, constants, out value);
    }

    /// <summary>Reads an array length in <c>[ ]</c>, the position at the <c>[</c>, reporting a
    /// length that is missing or not a constant.</summary>
    protected bool TryReadLength(out long length)
    {
        var open = Current;
        int close = FindClose(Mark);
        length = 0;
        if (close < 0)
        {
            Report("This [ is never closed.", open.Position);
            return false;
        }

        Advance();
        if (Mark == close)
        {
            Advance();
            Report("An array needs a length - one sized at run time cannot be laid out.", open.Position);
            return false;
        }

        bool read = TryEvaluateTo(close, out length);
        Advance();
        if (!read)
        {
            Report("This array length is not a constant that can be worked out here.", open.Position);
            return false;
        }

        if (length <= 0)
        {
            Report("An array length must be more than 0.", open.Position);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads an enum's body from its <c>{</c> to its <c>}</c>: members counting up from 0 or from
    /// the last value given, each value an expression that may name an earlier member. In C the
    /// members are constants anywhere after, as an array length can use them.
    /// </summary>
    protected IReadOnlyList<EnumMember> ReadEnumMembers()
    {
        var members = new List<EnumMember>();
        if (!Accept("{"))
            return members;

        var scope = new Dictionary<string, long>(Constants, StringComparer.Ordinal);
        long next = 0;
        while (!AtEnd && !Current.Is("}"))
        {
            if (Current.Is("["))
            {
                SkipBalanced();
                continue;
            }

            if (!AtIdentifier)
            {
                Advance();
                continue;
            }

            var name = Advance();
            long value = next;
            if (Accept("=") && !TryEvaluateExpression(scope, out value))
            {
                Report($"{name.Text}'s value is not a constant that can be worked out here.", name.Position);
                value = next;
            }

            members.Add(new EnumMember(name.Text, value));
            scope[name.Text] = value;
            if (Language == StructLanguage.C)
                Constants[name.Text] = value;
            next = value + 1;
            if (!Accept(","))
                break;
        }

        Accept("}");
        return members;
    }

    /// <summary>Renames a struct declared without a name once a declarator gives it one, as
    /// <c>typedef struct { … } Name;</c> does.</summary>
    protected void Rename(StructDeclaration declaration, string name)
    {
        int at = Declarations.IndexOf(declaration);
        if (at >= 0)
            Declarations[at] = declaration with { Name = name };
    }
}
