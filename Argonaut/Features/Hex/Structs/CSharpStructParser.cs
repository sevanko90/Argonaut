using System;
using System.Collections.Generic;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// Reads the struct declarations of C# source: <c>struct</c>, <c>record struct</c> and their
/// fields - including <c>fixed</c> buffers, auto-properties (whose backing field is stored like
/// any other) and the <c>[MarshalAs]</c> forms that size an array or string in place - and the
/// <c>const</c> integers lengths are written with. Methods, properties with bodies, static members
/// and every other kind of type are skipped.
/// </summary>
internal sealed class CSharpStructParser(string source) : StructSourceParser(source)
{
    private static readonly HashSet<string> Modifiers =
    [
        "public", "private", "protected", "internal", "unsafe", "readonly", "ref", "partial", "new",
        "sealed", "abstract", "file", "volatile", "required", "override", "virtual", "extern", "async",
    ];

    private static readonly Dictionary<string, string> SystemNames = new(StringComparer.Ordinal)
    {
        ["Byte"] = "byte",
        ["SByte"] = "sbyte",
        ["Int16"] = "short",
        ["UInt16"] = "ushort",
        ["Int32"] = "int",
        ["UInt32"] = "uint",
        ["Int64"] = "long",
        ["UInt64"] = "ulong",
        ["Single"] = "float",
        ["Double"] = "double",
        ["Boolean"] = "bool",
        ["Char"] = "char",
        ["IntPtr"] = "nint",
        ["UIntPtr"] = "nuint",
        ["String"] = "string",
    };

    /// <summary>What a field's attributes say about how it is stored.</summary>
    private readonly record struct MarshalAs(string? UnmanagedType, long? SizeConst, SourcePosition Position);

    protected override StructLanguage Language => StructLanguage.CSharp;

    protected override void ParseDeclarations(bool insideBraces)
    {
        var attributes = new List<Lexeme>();
        while (!AtEnd)
        {
            if (Current.Is("}"))
            {
                if (insideBraces)
                    return;
                Advance();
                continue;
            }

            if (Current.Is("["))
            {
                ReadAttributes(attributes);
                continue;
            }

            if (Current.Kind == LexemeKind.Directive || Accept(";"))
            {
                if (Current.Kind == LexemeKind.Directive)
                    Advance();
                continue;
            }

            if (Current.Is("namespace"))
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

                attributes.Clear();
                continue;
            }

            if (Current.Is("using"))
            {
                SkipStatement();
                continue;
            }

            while (AtIdentifier && (Modifiers.Contains(Current.Text) || Current.Is("static")))
                Advance();

            if (Current.Is("struct") || (Current.Is("record") && PeekAhead(1).Is("struct")))
            {
                ParseStruct(attributes);
            }
            else if (Current.Is("enum"))
            {
                ParseEnum(attributes);
            }
            else
            {
                SkipStatement();
            }

            attributes.Clear();
        }
    }

    /// <summary>Collects the lexemes of one or more <c>[ … ]</c> attribute lists.</summary>
    private void ReadAttributes(List<Lexeme> into)
    {
        while (Current.Is("["))
        {
            int close = FindClose(Mark);
            if (close < 0)
            {
                Advance();
                return;
            }

            while (Mark <= close)
                into.Add(Advance());
        }
    }

    private void ParseStruct(List<Lexeme> attributes)
    {
        if (Current.Is("record"))
            Advance();
        var keyword = Advance();
        if (!AtIdentifier)
        {
            Report("Expected the struct's name.", Current.Position);
            SkipStatement();
            return;
        }

        var name = Advance();
        var layout = ReadStructLayout(attributes, new StructDeclaration(name.Text, [], keyword.Position));
        if (Current.Is("<"))
        {
            Report("Generic structs are not supported.", Current.Position);
            SkipStatement();
            return;
        }

        var fields = new List<FieldDeclaration>();
        if (Current.Is("("))
            ReadPrimaryConstructor(fields);

        while (!AtEnd && !Current.Is("{") && !Current.Is(";"))
            Advance();

        if (Accept("{"))
        {
            var memberAttributes = new List<Lexeme>();
            while (!AtEnd && !Current.Is("}"))
            {
                if (Current.Is("["))
                {
                    ReadAttributes(memberAttributes);
                    continue;
                }

                ParseMember(fields, memberAttributes);
                memberAttributes.Clear();
            }

            Accept("}");
        }

        Accept(";");
        Declarations.Add(layout with { Fields = fields });
    }

    /// <summary>A <c>record struct</c>'s parameters are its fields.</summary>
    private void ReadPrimaryConstructor(List<FieldDeclaration> fields)
    {
        int close = FindClose(Mark);
        Advance();
        while (!AtEnd && Mark < close)
        {
            var attributes = new List<Lexeme>();
            ReadAttributes(attributes);
            if (TryReadType(out var type, out bool isArray) && AtIdentifier)
            {
                var name = Advance();
                AddField(fields, type, isArray, name, attributes, []);
            }

            while (!AtEnd && Mark < close && !Current.Is(","))
            {
                if (Current.Is("(") || Current.Is("[") || Current.Is("{"))
                    SkipBalanced();
                else
                    Advance();
            }

            Accept(",");
        }

        Accept(")");
    }

    /// <summary>Applies <c>[StructLayout(LayoutKind.X, Pack = n, Size = n, CharSet = …)]</c>
    /// to <paramref name="declaration"/>.</summary>
    private StructDeclaration ReadStructLayout(List<Lexeme> attributes, StructDeclaration declaration)
    {
        int at = attributes.FindIndex(l => l.Is("StructLayout") || l.Is("StructLayoutAttribute"));
        if (at < 0)
            return declaration;

        int depth = 0;
        for (int i = at + 1; i < attributes.Count; i++)
        {
            var lexeme = attributes[i];
            if (lexeme.Is("("))
                depth++;
            else if (lexeme.Is(")") && --depth == 0)
                break;
            else if (lexeme.Is("LayoutKind") && i + 2 < attributes.Count && attributes[i + 1].Is("."))
            {
                var kind = attributes[i + 2];
                if (kind.Is("Explicit"))
                    declaration = declaration with { IsExplicit = true };
                else if (kind.Is("Auto"))
                    Report("LayoutKind.Auto lets the runtime reorder the fields - it cannot be laid out here.", kind.Position);
            }
            else if (lexeme.Is("CharSet") && i + 3 < attributes.Count && attributes[i + 1].Is("="))
            {
                // CharSet = CharSet.Unicode: the value is the last name before the next , or ).
                int end = i + 2;
                while (end + 1 < attributes.Count && !attributes[end + 1].Is(",") && !attributes[end + 1].Is(")"))
                    end++;
                declaration = declaration with { HasWideStrings = attributes[end].Is("Unicode") };
            }
            else if ((lexeme.Is("Pack") || lexeme.Is("Size")) && i + 2 < attributes.Count && attributes[i + 1].Is("="))
            {
                int end = i + 2;
                while (end < attributes.Count && !attributes[end].Is(",") && !attributes[end].Is(")"))
                    end++;
                if (!ConstantExpression.TryEvaluate(attributes, i + 2, end, Constants, octalLiterals: false, out long value) || value < 0)
                    Report($"{lexeme.Text} is not a constant that can be worked out here.", lexeme.Position);
                else if (lexeme.Is("Size"))
                    declaration = declaration with { MinimumSize = value };
                else if (value is not (0 or 1 or 2 or 4 or 8 or 16 or 32 or 64 or 128))
                    Report("Pack must be 0, or a power of two up to 128.", lexeme.Position);
                else if (value > 0)
                    declaration = declaration with { Pack = (int)value };
            }
        }

        return declaration;
    }

    /// <summary>Reads <c>enum Name [: type] { … }</c>; <c>[Flags]</c> says its values combine.</summary>
    private void ParseEnum(List<Lexeme> attributes)
    {
        var keyword = Advance();
        if (!AtIdentifier)
        {
            SkipStatement();
            return;
        }

        var name = Advance();
        var underlying = new TypeReference("int");
        if (Accept(":") && !TryReadType(out underlying, out _))
        {
            SkipStatement();
            return;
        }

        if (!Current.Is("{"))
        {
            SkipStatement();
            return;
        }

        bool flags = attributes.Exists(l => l.Is("Flags") || l.Is("FlagsAttribute"));
        Enums.Add(new EnumDeclaration(name.Text, underlying, ReadEnumMembers(), flags, keyword.Position));
        Accept(";");
    }

    private void ParseMember(List<FieldDeclaration> fields, List<Lexeme> attributes)
    {
        if (Current.Kind == LexemeKind.Directive || Current.Is(";"))
        {
            Advance();
            return;
        }

        bool isStatic = false, isConst = false, isFixed = false;
        while (AtIdentifier && (Modifiers.Contains(Current.Text) || Current.Is("static") || Current.Is("const") || Current.Is("fixed")))
        {
            var modifier = Advance();
            isStatic |= modifier.Is("static");
            isConst |= modifier.Is("const");
            isFixed |= modifier.Is("fixed");
        }

        if (Current.Is("struct") || (Current.Is("record") && PeekAhead(1).Is("struct")))
        {
            ParseStruct(attributes);
            return;
        }

        if (Current.Is("enum"))
        {
            ParseEnum(attributes);
            return;
        }

        if (Current.Is("class") || Current.Is("interface") || Current.Is("delegate") || Current.Is("record")
            || Current.Is("event") || Current.Is("operator") || Current.Is("implicit") || Current.Is("explicit") || Current.Is("~"))
        {
            SkipStatement();
            return;
        }

        if (isConst)
        {
            ReadConstants();
            return;
        }

        if (isStatic)
        {
            SkipStatement();
            return;
        }

        var start = Current;
        int problems = Problems.Count;
        if (!TryReadType(out var type, out bool isArray, out var written))
        {
            if (Problems.Count == problems)
                Report("Expected a field.", start.Position);
            SkipStatement();
            return;
        }

        if (Current.Is("(") || Current.Is("this"))
        {
            // A constructor or an indexer.
            SkipStatement();
            return;
        }

        if (!AtIdentifier)
        {
            // Never dropped quietly: a member that is not read says so, or its bytes would shift
            // every field after it without a word.
            Report("Expected a field name after the type.", Current.Position);
            SkipStatement();
            return;
        }

        if (isFixed)
        {
            ReadFixedBuffers(fields, type, attributes);
            return;
        }

        while (!AtEnd)
        {
            var name = Advance();
            if (Current.Is("("))
            {
                SkipStatement();
                return;
            }

            // C's spelling, char Name[20], read as it is meant.
            var lengths = new List<long>(written);
            while (Current.Is("["))
            {
                if (!TryReadLength(out long length))
                {
                    SkipStatement();
                    return;
                }

                lengths.Add(length);
            }

            if (Current.Is("=>"))
            {
                SkipStatement();
                return;
            }

            if (Current.Is("{"))
            {
                if (IsAutoProperty())
                    AddField(fields, type, isArray, name, attributes, lengths);
                SkipStatement();
                return;
            }

            AddField(fields, type, isArray, name, attributes, lengths);
            if (Accept("="))
                SkipInitializer();
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

    /// <summary>Whether the property whose <c>{</c> is current has only <c>get;</c>,
    /// <c>set;</c> and <c>init;</c> accessors - a backing field the compiler stores in the struct.</summary>
    private bool IsAutoProperty()
    {
        int close = FindClose(Mark);
        int mark = Mark;
        Advance();
        bool auto = true;
        while (Mark < close)
        {
            var lexeme = Advance();
            if (lexeme.Is("get") || lexeme.Is("set") || lexeme.Is("init") || lexeme.Is(";") || Modifiers.Contains(lexeme.Text))
                continue;
            auto = false;
            break;
        }

        Rewind(mark);
        return auto;
    }

    private void ReadConstants()
    {
        if (!TryReadType(out _, out _))
        {
            SkipStatement();
            return;
        }

        while (AtIdentifier)
        {
            string name = Advance().Text;
            if (!Accept("="))
                break;
            if (TryEvaluateExpression(out long value))
                Constants[name] = value;
            else
                SkipInitializer();
            if (!Accept(","))
                break;
        }

        SkipStatement();
    }

    private void ReadFixedBuffers(List<FieldDeclaration> fields, TypeReference type, List<Lexeme> attributes)
    {
        while (AtIdentifier)
        {
            var name = Advance();
            if (!Current.Is("["))
            {
                Report("A fixed buffer needs its length: fixed byte Name[16].", Current.Position);
                SkipStatement();
                return;
            }

            if (!TryReadLength(out long length))
            {
                SkipStatement();
                return;
            }

            fields.Add(new FieldDeclaration(name.Text, type, [length], FieldStorage.Natural, name.Position) { ExplicitOffset = ReadFieldOffset(attributes) });
            if (!Accept(","))
                break;
        }

        SkipStatement();
    }

    /// <summary>Adds a field, applying what its <c>[MarshalAs]</c> says. A managed array or
    /// string is stored elsewhere, so without <c>ByValArray</c>/<c>ByValTStr</c> and a
    /// <c>SizeConst</c> it cannot be read from these bytes.</summary>
    /// <param name="written">Lengths written in the declaration - <c>char[20] Name</c> or
    /// <c>char Name[20]</c>, which C# does not allow but whose meaning is plain: an array stored in
    /// place, as a fixed buffer is.</param>
    private void AddField(List<FieldDeclaration> fields, TypeReference type, bool isArray, Lexeme name, List<Lexeme> attributes, IReadOnlyList<long> written)
    {
        var marshal = ReadMarshalAs(attributes);
        var storage = FieldStorage.Natural;
        IReadOnlyList<long> lengths = [];

        if (written.Count > 0 && !isArray && type.Name != "string")
        {
            fields.Add(new FieldDeclaration(name.Text, type, written, storage, name.Position) { ExplicitOffset = ReadFieldOffset(attributes) });
            return;
        }

        if (type.Name == "bool" && type.PointerDepth == 0 && !isArray)
        {
            storage = marshal?.UnmanagedType switch
            {
                "Bool" => FieldStorage.FourByteBool,
                "U1" or "I1" => FieldStorage.OneByteBool,
                _ => FieldStorage.Natural,
            };
        }

        if (isArray)
        {
            if (marshal is not { UnmanagedType: "ByValArray", SizeConst: > 0 } array)
            {
                Report($"{name.Text}: a managed array is not stored in the struct - mark it [MarshalAs(UnmanagedType.ByValArray, SizeConst = n)] or use a fixed buffer.", name.Position);
                return;
            }

            lengths = [array.SizeConst!.Value];
        }
        else if (type.Name == "string" && type.PointerDepth == 0)
        {
            if (marshal is not { UnmanagedType: "ByValTStr", SizeConst: > 0 } text)
            {
                Report($"{name.Text}: a string is not stored in the struct - mark it [MarshalAs(UnmanagedType.ByValTStr, SizeConst = n)].", name.Position);
                return;
            }

            storage = FieldStorage.AnsiString;
            lengths = [text.SizeConst!.Value];
        }

        fields.Add(new FieldDeclaration(name.Text, type, lengths, storage, name.Position) { ExplicitOffset = ReadFieldOffset(attributes) });
    }

    /// <summary>Reads <c>[FieldOffset(n)]</c> among a field's attributes.</summary>
    private long? ReadFieldOffset(List<Lexeme> attributes)
    {
        int at = attributes.FindIndex(l => l.Is("FieldOffset") || l.Is("FieldOffsetAttribute"));
        if (at < 0 || at + 1 >= attributes.Count || !attributes[at + 1].Is("("))
            return null;

        int end = at + 2;
        while (end < attributes.Count && !attributes[end].Is(")"))
            end++;
        if (ConstantExpression.TryEvaluate(attributes, at + 2, end, Constants, octalLiterals: false, out long offset) && offset >= 0)
            return offset;

        Report("FieldOffset is not a constant that can be worked out here.", attributes[at].Position);
        return null;
    }

    /// <summary>Finds <c>MarshalAs(UnmanagedType.X, SizeConst = n)</c> among a field's attributes.</summary>
    private MarshalAs? ReadMarshalAs(List<Lexeme> attributes)
    {
        int at = attributes.FindIndex(l => l.Is("MarshalAs") || l.Is("MarshalAsAttribute"));
        if (at < 0)
            return null;

        string? unmanaged = null;
        long? sizeConst = null;
        int depth = 0;
        for (int i = at + 1; i < attributes.Count; i++)
        {
            var lexeme = attributes[i];
            if (lexeme.Is("("))
                depth++;
            else if (lexeme.Is(")") && --depth == 0)
                break;
            else if (lexeme.Is("UnmanagedType") && i + 2 < attributes.Count && attributes[i + 1].Is("."))
                unmanaged ??= attributes[i + 2].Text;
            else if (lexeme.Is("SizeConst") && i + 2 < attributes.Count && attributes[i + 1].Is("="))
            {
                int end = i + 2;
                while (end < attributes.Count && !attributes[end].Is(",") && !attributes[end].Is(")"))
                    end++;
                if (ConstantExpression.TryEvaluate(attributes, i + 2, end, Constants, octalLiterals: false, out long size))
                    sizeConst = size;
                else
                    Report("SizeConst is not a constant that can be worked out here.", attributes[i + 2].Position);
            }
        }

        return new MarshalAs(unmanaged, sizeConst, attributes[at].Position);
    }

    private bool TryReadType(out TypeReference type, out bool isArray) => TryReadType(out type, out isArray, out _);

    /// <summary>Reads a type: a keyword or a (qualified) name, its pointers, and <c>[]</c> - or
    /// <c>[20]</c>, a length C# does not allow there but whose meaning is plain, returned in
    /// <paramref name="lengths"/>. <c>System.Int32</c> and the other framework names read as their
    /// keywords.</summary>
    private bool TryReadType(out TypeReference type, out bool isArray, out IReadOnlyList<long> lengths)
    {
        type = new TypeReference(string.Empty);
        isArray = false;
        lengths = [];
        if (!AtIdentifier)
            return false;

        string name = Advance().Text;
        while (Current.Is(".") && PeekAhead(1).Kind == LexemeKind.Identifier)
        {
            Advance();
            name = Advance().Text;
        }

        if (Current.Is("::") && PeekAhead(1).Kind == LexemeKind.Identifier)
        {
            Advance();
            name = Advance().Text;
        }

        if (Current.Is("<"))
        {
            Report($"Generic types such as {name}<…> are not supported.", Current.Position);
            return false;
        }

        if (Current.Is("?"))
        {
            Report($"Nullable {name}? is not supported - its layout is the runtime's own.", Current.Position);
            return false;
        }

        int pointers = 0;
        while (Accept("*"))
            pointers++;

        if (Current.Is("[") && PeekAhead(1).Is("]"))
        {
            Advance();
            Advance();
            isArray = true;
        }
        else if (Current.Is("["))
        {
            var written = new List<long>();
            while (Current.Is("["))
            {
                if (!TryReadLength(out long length))
                    return false;
                written.Add(length);
            }

            lengths = written;
        }

        type = new TypeReference(SystemNames.GetValueOrDefault(name, name), pointers);
        return true;
    }
}
