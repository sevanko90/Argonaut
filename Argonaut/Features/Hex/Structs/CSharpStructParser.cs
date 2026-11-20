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
        ReportLayoutAttributes(attributes);
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
        Declarations.Add(new StructDeclaration(name.Text, fields, keyword.Position));
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
                AddField(fields, type, isArray, name, attributes);
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

    private void ReportLayoutAttributes(List<Lexeme> attributes)
    {
        for (int i = 0; i < attributes.Count; i++)
        {
            var lexeme = attributes[i];
            if (lexeme.Is("Explicit") || lexeme.Is("Auto") && i > 0 && attributes[i - 1].Is("."))
                Report($"LayoutKind.{lexeme.Text} is not supported yet - the fields are laid out in sequence.", lexeme.Position);
            else if ((lexeme.Is("Pack") || lexeme.Is("Size") || lexeme.Is("CharSet")) && i + 1 < attributes.Count && attributes[i + 1].Is("="))
                Report($"StructLayout's {lexeme.Text} is not supported yet.", lexeme.Position);
        }
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

        if (Current.Is("class") || Current.Is("interface") || Current.Is("enum") || Current.Is("delegate") || Current.Is("record")
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
        if (!TryReadType(out var type, out bool isArray))
        {
            if (Problems.Count == problems)
                Report("Expected a field.", start.Position);
            SkipStatement();
            return;
        }

        if (Current.Is("(") || Current.Is("this") || !AtIdentifier)
        {
            // A constructor, an indexer, or something not read.
            SkipStatement();
            return;
        }

        if (isFixed)
        {
            ReadFixedBuffers(fields, type);
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

            if (Current.Is("=>"))
            {
                SkipStatement();
                return;
            }

            if (Current.Is("{"))
            {
                if (IsAutoProperty())
                    AddField(fields, type, isArray, name, attributes);
                SkipStatement();
                return;
            }

            AddField(fields, type, isArray, name, attributes);
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

    private void ReadFixedBuffers(List<FieldDeclaration> fields, TypeReference type)
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

            fields.Add(new FieldDeclaration(name.Text, type, [length], FieldStorage.Natural, name.Position));
            if (!Accept(","))
                break;
        }

        SkipStatement();
    }

    /// <summary>Adds a field, applying what its <c>[MarshalAs]</c> says. A managed array or
    /// string is stored elsewhere, so without <c>ByValArray</c>/<c>ByValTStr</c> and a
    /// <c>SizeConst</c> it cannot be read from these bytes.</summary>
    private void AddField(List<FieldDeclaration> fields, TypeReference type, bool isArray, Lexeme name, List<Lexeme> attributes)
    {
        var marshal = ReadMarshalAs(attributes);
        var storage = FieldStorage.Natural;
        IReadOnlyList<long> lengths = [];

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

        fields.Add(new FieldDeclaration(name.Text, type, lengths, storage, name.Position));
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

    /// <summary>Reads a type: a keyword or a (qualified) name, its pointers, and <c>[]</c>.
    /// <c>System.Int32</c> and the other framework names read as their keywords.</summary>
    private bool TryReadType(out TypeReference type, out bool isArray)
    {
        type = new TypeReference(string.Empty);
        isArray = false;
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

        type = new TypeReference(SystemNames.GetValueOrDefault(name, name), pointers);
        return true;
    }
}
