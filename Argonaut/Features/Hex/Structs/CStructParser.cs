using System;
using System.Collections.Generic;
using System.Linq;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// Reads the struct declarations of a C or C++ header: <c>struct</c> and <c>class</c> with their
/// fields, <c>typedef</c> and <c>using</c> aliases, and the integer constants array lengths are
/// written with (<c>#define</c>, <c>const</c>, <c>constexpr</c>). Functions, methods, globals and
/// other preprocessor lines are skipped.
/// </summary>
internal sealed class CStructParser(string source) : StructSourceParser(source)
{
    private static readonly HashSet<string> PrimitiveWords =
    [
        "signed", "unsigned", "short", "long", "int", "char", "float", "double", "bool", "_Bool",
        "void", "wchar_t", "char8_t", "char16_t", "char32_t",
    ];

    private static readonly HashSet<string> Qualifiers = ["const", "volatile", "mutable", "register", "inline", "extern"];

    private int anonymousCount;

    protected override StructLanguage Language => StructLanguage.C;

    protected override void ParseDeclarations(bool insideBraces)
    {
        while (!AtEnd)
        {
            if (Current.Is("}"))
            {
                if (insideBraces)
                    return;
                Advance();
                continue;
            }

            var lexeme = Current;
            if (lexeme.Kind == LexemeKind.Directive)
            {
                ReadDirective(Advance());
            }
            else if (Accept(";"))
            {
            }
            else if (lexeme.Is("typedef"))
            {
                ParseTypedef();
            }
            else if (lexeme.Is("using"))
            {
                ParseUsing();
            }
            else if (lexeme.Is("struct") || lexeme.Is("class"))
            {
                ParseStructStatement(out _);
            }
            else if (lexeme.Is("union"))
            {
                Report("Unions are not supported yet.", lexeme.Position);
                SkipStatement();
            }
            else if (lexeme.Is("namespace") || (lexeme.Is("extern") && PeekAhead(1).Kind == LexemeKind.Quoted && PeekAhead(2).Is("{")))
            {
                Advance();
                while (!AtEnd && !Current.Is("{") && !Current.Is(";"))
                    Advance();
                if (Accept("{"))
                {
                    ParseDeclarations(insideBraces: true);
                    Accept("}");
                }
                else
                {
                    Accept(";");
                }
            }
            else if (lexeme.Is("template"))
            {
                Advance();
                SkipAngles();
                SkipStatement();
            }
            else if (!TryReadConstant())
            {
                SkipStatement();
            }
        }
    }

    /// <summary>Reads <c>#define NAME value</c> when the value is an integer constant; every other
    /// directive is ignored.</summary>
    private void ReadDirective(Lexeme directive)
    {
        if (!directive.Text.StartsWith("define", StringComparison.Ordinal))
            return;

        var parts = StructLexer.Read(directive.Text["define".Length..]);
        if (parts.Count < 3 || parts[0].Kind != LexemeKind.Identifier)
            return;

        // A function-like macro's parenthesis follows the name directly; the lexer keeps no
        // spacing, so one whose body starts with a parenthesis is told apart by its column.
        if (parts[1].Is("(") && parts[1].Position.Column == parts[0].Position.Column + parts[0].Text.Length)
            return;

        if (ConstantExpression.TryEvaluate(parts, 1, parts.Count - 1, Constants, octalLiterals: true, out long value))
            Constants[parts[0].Text] = value;
    }

    private void SkipAngles()
    {
        if (!Current.Is("<"))
            return;

        int depth = 0;
        while (!AtEnd)
        {
            var lexeme = Advance();
            if (lexeme.Is("<"))
                depth++;
            else if (lexeme.Is(">"))
                depth--;
            else if (lexeme.Is(">>"))
                depth -= 2;
            if (depth <= 0)
                return;
        }
    }

    /// <summary>Reads <c>[static] const|constexpr type NAME = value;</c> as a constant, or rewinds.</summary>
    private bool TryReadConstant()
    {
        int mark = Mark;
        bool constant = false;
        while (Current.Is("static") || Current.Is("const") || Current.Is("constexpr") || Current.Is("inline"))
        {
            constant |= !Current.Is("static") && !Current.Is("inline");
            Advance();
        }

        if (constant && TryReadType(out _, report: false) && AtIdentifier)
        {
            string name = Advance().Text;
            if (Accept("=") || Current.Is("{"))
            {
                bool braced = Accept("{");
                if (TryEvaluateExpression(out long value) && (!braced || Accept("}")) && Accept(";"))
                {
                    Constants[name] = value;
                    return true;
                }
            }
        }

        Rewind(mark);
        return false;
    }

    /// <summary>Reads <c>typedef</c>: a struct defined in it, an alias of a struct's tag, or an
    /// alias of any other type.</summary>
    private void ParseTypedef()
    {
        var position = Advance().Position;
        if (Current.Is("union") || Current.Is("enum"))
        {
            if (Current.Is("union"))
                Report("Unions are not supported yet.", Current.Position);
            SkipStatement();
            return;
        }

        TypeReference aliased;
        StructDeclaration? defined = null;
        if ((Current.Is("struct") || Current.Is("class")) && (PeekAhead(1).Is("{") || PeekAhead(2).Is("{") || PeekAhead(2).Is(":")))
        {
            Advance();
            string? tag = AtIdentifier ? Advance().Text : null;
            defined = ParseStructBody(tag ?? NextAnonymousName(), position);
            if (defined is null)
            {
                SkipStatement();
                return;
            }

            aliased = new TypeReference(defined.Name);
        }
        else if (!TryReadType(out aliased, report: true))
        {
            SkipStatement();
            return;
        }

        bool named = false;
        while (!AtEnd && !Current.Is(";"))
        {
            int pointers = ReadPointers();
            bool functionPointer = false;
            if (Current.Is("(") && PeekAhead(1).Is("*"))
            {
                Advance();
                pointers += ReadPointers();
                functionPointer = true;
            }

            if (!AtIdentifier)
            {
                Report("Expected the name this typedef declares.", Current.Position);
                SkipStatement();
                return;
            }

            var nameLexeme = Advance();
            if (functionPointer)
            {
                Accept(")");
                if (Current.Is("("))
                    SkipBalanced();
            }

            if (Current.Is("["))
            {
                Report("A typedef of an array is not supported - declare the array on the field.", Current.Position);
                SkipStatement();
                return;
            }

            if (defined is not null && !named && pointers == 0 && defined.Name.StartsWith('<'))
            {
                Rename(defined, nameLexeme.Text);
                aliased = new TypeReference(nameLexeme.Text);
                named = true;
            }
            else if (nameLexeme.Text != aliased.Name || pointers > 0)
            {
                Aliases[nameLexeme.Text] = functionPointer
                    ? new TypeReference("void", 1)
                    : aliased with { PointerDepth = aliased.PointerDepth + pointers };
            }

            if (!Accept(","))
                break;
        }

        Accept(";");
    }

    /// <summary>Reads <c>using Name = type;</c> as an alias; any other <c>using</c> is skipped.</summary>
    private void ParseUsing()
    {
        Advance();
        if (AtIdentifier && PeekAhead(1).Is("="))
        {
            string name = Advance().Text;
            Advance();
            if (TryReadType(out var aliased, report: true))
            {
                aliased = aliased with { PointerDepth = aliased.PointerDepth + ReadPointers() };
                if (Accept(";"))
                {
                    Aliases[name] = aliased;
                    return;
                }
            }
        }

        SkipStatement();
    }

    /// <summary>
    /// Reads a statement starting with <c>struct</c> or <c>class</c>: a definition, with any
    /// declarators after it; a forward declaration; or, inside a struct, a field whose type is
    /// written with the keyword. Returns the struct it defined, if it defined one.
    /// </summary>
    private void ParseStructStatement(out StructDeclaration? defined)
    {
        defined = null;
        var keyword = Advance();
        string? name = AtIdentifier ? Current.Text : null;
        int bodyAt = name is null ? 0 : 1;
        if (!PeekAhead(bodyAt).Is("{") && !PeekAhead(bodyAt).Is(":") && !(bodyAt == 1 && PeekAhead(1).Is("final")))
        {
            // A forward declaration, or a variable of an existing struct.
            SkipStatement();
            return;
        }

        if (name is not null)
            Advance();
        defined = ParseStructBody(name ?? NextAnonymousName(), keyword.Position);
        if (defined is null)
        {
            SkipStatement();
            return;
        }

        if (!Accept(";"))
            SkipStatement();
    }

    private string NextAnonymousName() => $"<anonymous {++this.anonymousCount}>";

    /// <summary>Reads a struct's body from its <c>{</c> (or base list) to its <c>}</c>, adding it
    /// to the declarations; null when it cannot be read.</summary>
    private StructDeclaration? ParseStructBody(string name, SourcePosition position)
    {
        Accept("final");
        if (Current.Is(":"))
        {
            Report("Inheritance is not supported yet.", Current.Position);
            while (!AtEnd && !Current.Is("{"))
                Advance();
        }

        if (!Accept("{"))
        {
            Report($"Expected {{ to open {name}.", Current.Position);
            return null;
        }

        var fields = new List<FieldDeclaration>();
        while (!AtEnd && !Current.Is("}"))
            ParseMember(fields);

        Accept("}");
        var declaration = new StructDeclaration(name, fields, position);
        Declarations.Add(declaration);
        return declaration;
    }

    private void ParseMember(List<FieldDeclaration> fields)
    {
        var lexeme = Current;
        if (lexeme.Kind == LexemeKind.Directive)
        {
            ReadDirective(Advance());
            return;
        }

        if (Accept(";"))
            return;

        if ((lexeme.Is("public") || lexeme.Is("private") || lexeme.Is("protected")) && PeekAhead(1).Is(":"))
        {
            Advance();
            Advance();
            return;
        }

        if ((lexeme.Is("struct") || lexeme.Is("class"))
            && (PeekAhead(1).Is("{") || (PeekAhead(1).Kind == LexemeKind.Identifier && (PeekAhead(2).Is("{") || PeekAhead(2).Is(":")))))
        {
            ParseNestedStruct(fields);
            return;
        }

        if (lexeme.Is("union"))
        {
            Report("Unions are not supported yet.", lexeme.Position);
            SkipStatement();
            return;
        }

        if (lexeme.Is("typedef"))
        {
            ParseTypedef();
            return;
        }

        if (lexeme.Is("using"))
        {
            ParseUsing();
            return;
        }

        if (lexeme.Is("virtual"))
        {
            Report("A virtual member adds a hidden vtable pointer, which is not laid out - remove it to read the fields.", lexeme.Position);
            SkipStatement();
            return;
        }

        if (lexeme.Is("enum") || lexeme.Is("friend") || lexeme.Is("template") || lexeme.Is("~") || lexeme.Is("operator")
            || lexeme.Is("static_assert"))
        {
            SkipStatement();
            return;
        }

        if (lexeme.Is("static") || lexeme.Is("constexpr"))
        {
            if (!TryReadConstant())
                SkipStatement();
            return;
        }

        ParseFields(fields);
    }

    /// <summary>Reads a struct defined inside another, and the fields declared with it
    /// (<c>struct Inner { … } inner, pair[2];</c>).</summary>
    private void ParseNestedStruct(List<FieldDeclaration> fields)
    {
        var keyword = Advance();
        string? name = AtIdentifier ? Advance().Text : null;
        var nested = ParseStructBody(name ?? NextAnonymousName(), keyword.Position);
        if (nested is null)
        {
            SkipStatement();
            return;
        }

        if (Accept(";"))
        {
            if (name is null)
                Report("An anonymous struct without a field name is not supported yet.", keyword.Position);
            return;
        }

        ReadDeclarators(new TypeReference(nested.Name), fields);
    }

    /// <summary>Reads a field declaration: a type, then one or more declarators. A method or
    /// constructor is skipped.</summary>
    private void ParseFields(List<FieldDeclaration> fields)
    {
        var start = Current;
        if (!TryReadType(out var type, report: true))
        {
            SkipStatement();
            return;
        }

        if (Current.Is("(") && !PeekAhead(1).Is("*"))
        {
            // A constructor: its name read as the type.
            SkipStatement();
            return;
        }

        if (Current.Is(";") && start.Kind != LexemeKind.Identifier)
        {
            Advance();
            return;
        }

        ReadDeclarators(type, fields);
    }

    /// <summary>Reads <c>*name[N][M] = init, *other;</c> after a field's type.</summary>
    private void ReadDeclarators(TypeReference type, List<FieldDeclaration> fields)
    {
        while (!AtEnd)
        {
            int pointers = ReadPointers();
            if (Current.Is("&"))
            {
                Report("A reference member cannot be laid out - use a pointer.", Current.Position);
                SkipStatement();
                return;
            }

            bool functionPointer = false;
            if (Current.Is("(") && PeekAhead(1).Is("*"))
            {
                Advance();
                pointers += ReadPointers();
                functionPointer = true;
            }

            if (!AtIdentifier || Current.Is("operator"))
            {
                if (!Current.Is(";"))
                    Report("Expected a field name.", Current.Position);
                SkipStatement();
                return;
            }

            var name = Advance();
            if (functionPointer)
            {
                Accept(")");
                if (Current.Is("("))
                    SkipBalanced();
            }
            else if (Current.Is("("))
            {
                // A method.
                SkipStatement();
                return;
            }

            var lengths = new List<long>();
            while (Current.Is("["))
            {
                if (!TryReadLength(out long length))
                {
                    SkipStatement();
                    return;
                }

                lengths.Add(length);
            }

            if (Current.Is(":"))
            {
                Report("Bitfields are not supported yet.", Current.Position);
                SkipStatement();
                return;
            }

            if (Accept("="))
                SkipInitializer();
            else if (Current.Is("{"))
                SkipBalanced();

            var fieldType = functionPointer ? new TypeReference("void", pointers) : type with { PointerDepth = type.PointerDepth + pointers };
            fields.Add(new FieldDeclaration(name.Text, fieldType, lengths, FieldStorage.Natural, name.Position));

            if (Accept(","))
                continue;

            if (!Accept(";"))
            {
                Report("Expected ; after the field.", Current.Position);
                SkipStatement();
            }

            return;
        }
    }

    private int ReadPointers()
    {
        int pointers = 0;
        while (Current.Is("*") || Current.Is("const") || Current.Is("volatile") || Current.Is("__restrict") || Current.Is("restrict"))
        {
            if (Advance().Is("*"))
                pointers++;
        }

        return pointers;
    }

    /// <summary>
    /// Reads a type: qualifiers, then either C's multi-word primitive (<c>unsigned long int</c>,
    /// spelled canonically as <c>unsigned long</c>), a type written with its keyword
    /// (<c>struct Point</c>), or a name (<c>uint32_t</c>, <c>std::uint32_t</c>, <c>DWORD</c>).
    /// </summary>
    private bool TryReadType(out TypeReference type, bool report)
    {
        type = new TypeReference(string.Empty);
        while (Qualifiers.Contains(Current.Text) && AtIdentifier)
            Advance();

        var start = Current;
        if (Current.Is("struct") || Current.Is("class") || Current.Is("union") || Current.Is("enum"))
        {
            Advance();
            if (!AtIdentifier)
            {
                if (report)
                    Report("Expected a type name.", Current.Position);
                return false;
            }

            type = new TypeReference(ReadQualifiedName());
        }
        else if (AtIdentifier && PrimitiveWords.Contains(Current.Text))
        {
            var words = new List<string>();
            while (AtIdentifier && (PrimitiveWords.Contains(Current.Text) || Qualifiers.Contains(Current.Text)))
            {
                var word = Advance().Text;
                if (PrimitiveWords.Contains(word))
                    words.Add(word);
            }

            string? canonical = CanonicalPrimitive(words);
            if (canonical is null)
            {
                if (report)
                    Report($"'{string.Join(' ', words)}' is not a type that can be laid out.", start.Position);
                return false;
            }

            type = new TypeReference(canonical);
        }
        else if (AtIdentifier || Current.Is("::"))
        {
            type = new TypeReference(ReadQualifiedName());
        }
        else
        {
            if (report)
                Report("Expected a field.", start.Position);
            return false;
        }

        if (Current.Is("<"))
        {
            if (report)
                Report($"Templates such as {type.Name}<…> are not supported.", Current.Position);
            return false;
        }

        while (Current.Is("const") || Current.Is("volatile"))
            Advance();
        return true;
    }

    /// <summary>Reads <c>a::b::Name</c> as <c>Name</c> - types are found by their own name.</summary>
    private string ReadQualifiedName()
    {
        Accept("::");
        string name = Advance().Text;
        while (Current.Is("::") && PeekAhead(1).Kind == LexemeKind.Identifier)
        {
            Advance();
            name = Advance().Text;
        }

        return name;
    }

    private static string? CanonicalPrimitive(List<string> words)
    {
        bool unsigned = words.Contains("unsigned");
        bool signed = words.Contains("signed");
        int longs = words.Count(w => w == "long");
        var rest = words.Where(w => w is not ("unsigned" or "signed" or "long" or "short" or "int")).ToList();
        bool isShort = words.Contains("short");
        if (unsigned && signed || rest.Count > 1)
            return null;

        string? basic = rest.Count == 1 ? rest[0] : null;
        switch (basic)
        {
            case "char":
                return unsigned ? "unsigned char" : signed ? "signed char" : "char";
            case "double":
                return longs == 1 && !unsigned && !signed ? "long double" : longs == 0 && !unsigned && !signed ? "double" : null;
            case "_Bool":
                return "bool";
            case null:
                break;
            default:
                return unsigned || signed || longs > 0 || isShort ? null : basic;
        }

        string prefix = unsigned ? "unsigned " : string.Empty;
        if (isShort)
            return longs == 0 ? prefix + "short" : null;

        return longs switch
        {
            0 => prefix + "int",
            1 => prefix + "long",
            2 => prefix + "long long",
            _ => null,
        };
    }
}
