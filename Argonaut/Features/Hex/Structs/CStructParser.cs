using System;
using System.Collections.Generic;
using System.Linq;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// Reads the struct declarations of a C or C++ header: <c>struct</c>, <c>class</c> and
/// <c>union</c> with their fields and bitfields, a single base, <c>enum</c>s, <c>typedef</c> and
/// <c>using</c> aliases, the integer constants array lengths are written with (<c>#define</c>,
/// <c>const</c>, <c>constexpr</c>, enum members), and what changes a layout: <c>#pragma pack</c>,
/// <c>packed</c>, <c>aligned</c> and <c>alignas</c>. Functions, methods, globals and other
/// preprocessor lines are skipped.
/// </summary>
internal sealed class CStructParser(string source) : StructSourceParser(source)
{
    private static readonly HashSet<string> PrimitiveWords =
    [
        "signed", "unsigned", "short", "long", "int", "char", "float", "double", "bool", "_Bool",
        "void", "wchar_t", "char8_t", "char16_t", "char32_t",
    ];

    private static readonly HashSet<string> Qualifiers = ["const", "volatile", "mutable", "register", "inline", "extern"];

    private readonly Stack<int?> packs = new();

    // Whether the record being read is still plain data: set false by a constructor or a private
    // or protected section, and saved around a nested record.
    private bool plainData = true;
    private int? pack;
    private int anonymousCount;

    /// <summary>What attributes on a declaration ask for.</summary>
    private struct Attributes
    {
        public bool Packed;
        public int? Aligned;

        public readonly int? Alignment(int? other) => Aligned is { } a && other is { } b ? Math.Max(a, b) : Aligned ?? other;
    }

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
            else if (IsRecordKeyword(lexeme))
            {
                ParseRecordStatement(null);
            }
            else if (lexeme.Is("enum"))
            {
                ParseEnumStatement();
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

    private static bool IsRecordKeyword(Lexeme lexeme) => lexeme.Is("struct") || lexeme.Is("class") || lexeme.Is("union");

    /// <summary>Reads <c>#define NAME value</c> when the value is an integer constant, and
    /// <c>#pragma pack</c>; every other directive is ignored.</summary>
    private void ReadDirective(Lexeme directive)
    {
        if (directive.Text.StartsWith("pragma", StringComparison.Ordinal))
        {
            ReadPragma(StructLexer.Read(directive.Text["pragma".Length..]));
            return;
        }

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

    /// <summary><c>pack(n)</c>, <c>pack()</c>, <c>pack(push[, n])</c>, <c>pack(pop)</c>: the
    /// packing every struct defined after it is laid out with.</summary>
    private void ReadPragma(List<Lexeme> parts)
    {
        if (parts.Count < 3 || !parts[0].Is("pack") || !parts[1].Is("("))
            return;

        int? number = null;
        bool push = false, pop = false;
        for (int i = 2; i < parts.Count && !parts[i].Is(")"); i++)
        {
            if (parts[i].Is("push"))
                push = true;
            else if (parts[i].Is("pop"))
                pop = true;
            else if (parts[i].Kind == LexemeKind.Number && ConstantExpression.TryParseLiteral(parts[i].Text, true, out long n) && n > 0)
                number = (int)n;
        }

        if (pop)
        {
            this.pack = this.packs.Count > 0 ? this.packs.Pop() : null;
            return;
        }

        if (push)
            this.packs.Push(this.pack);
        if (number is not null || !push)
            this.pack = number;
    }

    /// <summary>
    /// Reads any attributes at the position: GCC's <c>__attribute__((packed, aligned(n)))</c>,
    /// MSVC's <c>__declspec(align(n))</c>, <c>alignas(n)</c> and <c>[[ … ]]</c>, keeping what
    /// changes a layout.
    /// </summary>
    private void ReadAttributes(ref Attributes attributes)
    {
        while (true)
        {
            if (Current.Is("__attribute__") || Current.Is("__declspec"))
            {
                Advance();
                int close = FindClose(Mark);
                if (close < 0)
                    return;

                while (Mark < close)
                {
                    var word = Advance();
                    if (word.Is("packed") || word.Is("__packed__"))
                        attributes.Packed = true;
                    else if ((word.Is("aligned") || word.Is("__aligned__") || word.Is("align")) && Current.Is("("))
                        attributes.Aligned = ReadAlignment() ?? attributes.Aligned;
                }

                Advance();
            }
            else if (Current.Is("alignas") || Current.Is("_Alignas"))
            {
                Advance();
                if (Current.Is("("))
                    attributes.Aligned = ReadAlignment() ?? attributes.Aligned;
            }
            else if (Current.Is("[") && PeekAhead(1).Is("["))
            {
                SkipBalanced();
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Reads <c>(n)</c> as an alignment, reporting one that is not a constant power of two.</summary>
    private int? ReadAlignment()
    {
        var open = Current;
        int close = FindClose(Mark);
        Advance();
        if (close < 0 || !TryEvaluateTo(close, out long value) || value <= 0 || (value & (value - 1)) != 0)
        {
            Report("An alignment must be a constant power of two.", open.Position);
            if (close >= 0)
                Advance();
            return null;
        }

        Advance();
        return (int)value;
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

    /// <summary>Whether a record keyword here starts a definition (a body or base list follows)
    /// rather than naming a type.</summary>
    private bool AtRecordDefinition()
    {
        int at = 1;
        while (PeekAhead(at).Is("__attribute__") || PeekAhead(at).Is("__declspec") || PeekAhead(at).Is("alignas") || PeekAhead(at).Is("_Alignas"))
        {
            at++;
            if (!PeekAhead(at).Is("("))
                return false;

            int close = FindClose(Mark + at);
            if (close < 0)
                return false;
            at = close - Mark + 1;
        }

        if (PeekAhead(at).Is("{"))
            return true;

        if (PeekAhead(at).Kind == LexemeKind.Identifier && !PeekAhead(at).Is("final"))
        {
            at++;
            if (PeekAhead(at).Is("final"))
                at++;
            return PeekAhead(at).Is("{") || PeekAhead(at).Is(":") || PeekAhead(at).Is("__attribute__");
        }

        return false;
    }

    /// <summary>
    /// Reads a statement starting with <c>struct</c>, <c>class</c> or <c>union</c>: a definition,
    /// with any declarators after it (added to <paramref name="fields"/> inside a struct); a
    /// forward declaration; or, inside a struct, a field whose type is written with the keyword.
    /// </summary>
    private void ParseRecordStatement(List<FieldDeclaration>? fields)
    {
        if (!AtRecordDefinition())
        {
            if (fields is not null)
                ParseFields(fields);
            else
                SkipStatement();
            return;
        }

        var keyword = Current;
        var defined = ParseRecordDefinition(out bool named);
        if (defined is null)
        {
            SkipStatement();
            return;
        }

        if (Accept(";"))
        {
            if (!named && fields is not null)
            {
                // An anonymous member: its fields read as the parent's, at its place.
                fields.Add(new FieldDeclaration(string.Empty, new TypeReference(defined.Name), [], FieldStorage.Natural, keyword.Position));
            }

            return;
        }

        if (fields is not null)
            ReadDeclarators(new TypeReference(defined.Name), fields, default);
        else
            SkipStatement();
    }

    private string NextAnonymousName() => $"<anonymous {++this.anonymousCount}>";

    /// <summary>Reads <c>struct|class|union [attributes] [Name] [: Base] { … } [attributes]</c>,
    /// adding the declaration; null when it cannot be read.</summary>
    private StructDeclaration? ParseRecordDefinition(out bool named)
    {
        var keyword = Advance();
        var attributes = new Attributes();
        ReadAttributes(ref attributes);
        named = AtIdentifier && !Current.Is("final");
        string name = named ? Advance().Text : NextAnonymousName();
        Accept("final");
        ReadAttributes(ref attributes);

        TypeReference? baseType = null;
        if (Accept(":"))
        {
            var bases = new List<TypeReference>();
            while (!AtEnd && !Current.Is("{"))
            {
                while (Current.Is("public") || Current.Is("private") || Current.Is("protected"))
                    Advance();
                if (Current.Is("virtual"))
                {
                    Report("Virtual inheritance is not supported.", Current.Position);
                    return null;
                }

                if (!AtIdentifier && !Current.Is("::"))
                    break;
                bases.Add(new TypeReference(ReadQualifiedName()));
                if (Current.Is("<"))
                {
                    Report("A template base is not supported.", Current.Position);
                    return null;
                }

                if (!Accept(","))
                    break;
            }

            if (bases.Count > 1)
            {
                Report("Only single inheritance is supported.", keyword.Position);
                return null;
            }

            baseType = bases.FirstOrDefault();
        }

        if (!Accept("{"))
        {
            Report($"Expected {{ to open {name}.", Current.Position);
            return null;
        }

        // Added before its fields are read, so a nested record's declaration comes after it.
        var declaration = new StructDeclaration(name, [], keyword.Position);
        Declarations.Add(declaration);

        bool outerPlainData = this.plainData;
        this.plainData = baseType is null;
        var fields = new List<FieldDeclaration>();
        while (!AtEnd && !Current.Is("}"))
            ParseMember(fields, name);
        Accept("}");
        bool plainData = this.plainData;
        this.plainData = outerPlainData;
        ReadAttributes(ref attributes);

        var complete = declaration with
        {
            Fields = fields,
            Kind = keyword.Is("union") ? StructKind.Union : StructKind.Struct,
            Pack = attributes.Packed ? 1 : this.pack,
            Alignment = attributes.Aligned,
            Base = baseType,
            IsPlainData = plainData,
        };
        Declarations[Declarations.IndexOf(declaration)] = complete;
        return complete;
    }

    /// <summary>Reads an enum definition and any declarators after it, or a field typed with
    /// <c>enum Name</c>.</summary>
    private void ParseEnumStatement(List<FieldDeclaration>? fields = null)
    {
        int mark = Mark;
        var defined = TryParseEnumDefinition();
        if (defined is null)
        {
            Rewind(mark);
            if (fields is not null)
                ParseFields(fields);
            else
                SkipStatement();
            return;
        }

        if (Accept(";"))
            return;

        if (fields is not null)
            ReadDeclarators(new TypeReference(defined.Name), fields, default);
        else
            SkipStatement();
    }

    /// <summary>Reads <c>enum [class|struct] [Name] [: type] { … }</c>, adding it; null when
    /// this is not a definition.</summary>
    private EnumDeclaration? TryParseEnumDefinition()
    {
        var keyword = Advance();
        if (Current.Is("class") || Current.Is("struct"))
            Advance();
        string name = AtIdentifier ? Advance().Text : NextAnonymousName();

        var underlying = new TypeReference("int");
        if (Accept(":") && !TryReadType(out underlying, report: true))
            return null;

        if (!Current.Is("{"))
            return null;

        var declaration = new EnumDeclaration(name, underlying, ReadEnumMembers(), IsFlags: false, keyword.Position);
        Enums.Add(declaration);
        return declaration;
    }

    /// <summary>Reads <c>typedef</c>: a struct, union or enum defined in it, an alias of a tag, or
    /// an alias of any other type.</summary>
    private void ParseTypedef()
    {
        Advance();
        TypeReference aliased;
        string? anonymous = null;
        if (IsRecordKeyword(Current) && AtRecordDefinition())
        {
            var defined = ParseRecordDefinition(out bool named);
            if (defined is null)
            {
                SkipStatement();
                return;
            }

            aliased = new TypeReference(defined.Name);
            anonymous = named ? null : defined.Name;
        }
        else if (Current.Is("enum") && (PeekAhead(1).Is("{") || PeekAhead(2).Is("{") || PeekAhead(2).Is(":") || PeekAhead(1).Is(":")))
        {
            var defined = TryParseEnumDefinition();
            if (defined is null)
            {
                SkipStatement();
                return;
            }

            aliased = new TypeReference(defined.Name);
            anonymous = defined.Name.StartsWith('<') ? defined.Name : null;
        }
        else if (!TryReadType(out aliased, report: true))
        {
            SkipStatement();
            return;
        }

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

            var ignored = new Attributes();
            ReadAttributes(ref ignored);

            if (anonymous is not null && pointers == 0 && !functionPointer)
            {
                // The first plain name names a struct, union or enum declared without one.
                RenameAnonymous(anonymous, nameLexeme.Text);
                aliased = new TypeReference(nameLexeme.Text);
                anonymous = null;
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

    private void RenameAnonymous(string anonymous, string name)
    {
        int record = Declarations.FindIndex(d => d.Name == anonymous);
        if (record >= 0)
        {
            Declarations[record] = Declarations[record] with { Name = name };
            return;
        }

        int enumeration = Enums.FindIndex(e => e.Name == anonymous);
        if (enumeration >= 0)
            Enums[enumeration] = Enums[enumeration] with { Name = name };
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

    private void ParseMember(List<FieldDeclaration> fields, string recordName)
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
            this.plainData &= lexeme.Is("public");
            Advance();
            Advance();
            return;
        }

        if (lexeme.Is(recordName) && PeekAhead(1).Is("("))
        {
            // A constructor.
            this.plainData = false;
            SkipStatement();
            return;
        }

        if (IsRecordKeyword(lexeme))
        {
            ParseRecordStatement(fields);
            return;
        }

        if (lexeme.Is("enum"))
        {
            ParseEnumStatement(fields);
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

        if (lexeme.Is("friend") || lexeme.Is("template") || lexeme.Is("~") || lexeme.Is("operator") || lexeme.Is("static_assert"))
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

    /// <summary>Reads a field declaration: attributes, a type, then one or more declarators, or an
    /// unnamed bitfield. A method or constructor is skipped.</summary>
    private void ParseFields(List<FieldDeclaration> fields)
    {
        var attributes = new Attributes();
        ReadAttributes(ref attributes);
        var start = Current;
        if (!TryReadType(out var type, report: true))
        {
            SkipStatement();
            return;
        }

        ReadAttributes(ref attributes);
        if (Current.Is("(") && !PeekAhead(1).Is("*"))
        {
            // A constructor: its name read as the type.
            SkipStatement();
            return;
        }

        if (Current.Is(":"))
        {
            // An unnamed bitfield: padding, or with width 0 a break to the type's boundary.
            var colon = Advance();
            if (!TryEvaluateExpression(out long width) || width < 0)
            {
                Report("A bitfield's width must be a constant of 0 or more.", colon.Position);
                SkipStatement();
                return;
            }

            fields.Add(new FieldDeclaration(string.Empty, type, [], FieldStorage.Natural, start.Position) { BitWidth = (int)width });
            if (!Accept(";"))
                SkipStatement();
            return;
        }

        ReadDeclarators(type, fields, attributes);
    }

    /// <summary>Reads <c>*name[N][M] = init, *other, bits : 3;</c> after a field's type.</summary>
    private void ReadDeclarators(TypeReference type, List<FieldDeclaration> fields, Attributes attributes)
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

            int? bitWidth = null;
            if (Current.Is(":"))
            {
                var colon = Advance();
                if (!TryEvaluateExpression(out long width) || width <= 0)
                {
                    Report("A named bitfield's width must be a constant of 1 or more.", colon.Position);
                    SkipStatement();
                    return;
                }

                bitWidth = (int)width;
            }

            var fieldAttributes = attributes;
            ReadAttributes(ref fieldAttributes);
            if (Accept("="))
                SkipInitializer();
            else if (Current.Is("{"))
                SkipBalanced();

            var fieldType = functionPointer ? new TypeReference("void", pointers) : type with { PointerDepth = type.PointerDepth + pointers };
            fields.Add(new FieldDeclaration(name.Text, fieldType, lengths, FieldStorage.Natural, name.Position)
            {
                BitWidth = bitWidth,
                Alignment = fieldAttributes.Packed ? null : fieldAttributes.Aligned,
            });

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
    /// (<c>struct Point</c>, <c>enum Mode</c>), or a name (<c>uint32_t</c>, <c>std::uint32_t</c>,
    /// <c>DWORD</c>).
    /// </summary>
    private bool TryReadType(out TypeReference type, bool report)
    {
        type = new TypeReference(string.Empty);
        while (Qualifiers.Contains(Current.Text) && AtIdentifier)
            Advance();

        var start = Current;
        if (IsRecordKeyword(Current) || Current.Is("enum"))
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
