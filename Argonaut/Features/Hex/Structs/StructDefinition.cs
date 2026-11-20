using System;
using System.Collections.Generic;

namespace Argonaut.Features.Hex.Structs;

/// <summary>The language a struct definition is written in. It decides the names of the types and
/// how much a few of them are stored in.</summary>
public enum StructLanguage
{
    C,
    CSharp,
}

/// <summary>Where something is in the definition's text, 1-based, so a problem can point at it.</summary>
public readonly record struct SourcePosition(int Line, int Column)
{
    public override string ToString() => $"line {Line}, column {Column}";
}

/// <summary>Something in a definition that cannot be read or laid out, and where.</summary>
public sealed record StructProblem(string Message, SourcePosition Position);

/// <summary>A type as a declaration names it: a canonical spelling (<c>unsigned long</c> for
/// <c>unsigned long int</c>, <c>int</c> for <c>System.Int32</c>) and how many pointers deep.
/// Resolved to a size only by the layout pass, since C's <c>long</c> depends on the target.</summary>
public sealed record TypeReference(string Name, int PointerDepth = 0)
{
    public override string ToString() => PointerDepth == 0 ? Name : Name + new string('*', PointerDepth);
}

/// <summary>How a field is stored when its declaration says more than its type does - the C#
/// marshalling attributes, which change a field's size without changing its type.</summary>
public enum FieldStorage
{
    /// <summary>As the type is stored in memory.</summary>
    Natural,

    /// <summary>A <c>bool</c> marshalled as Win32 <c>BOOL</c>: 4 bytes.</summary>
    FourByteBool,

    /// <summary>A <c>bool</c> marshalled as one byte, as it is stored anyway.</summary>
    OneByteBool,

    /// <summary>A <c>string</c> marshalled in place as <c>ByValTStr</c>: one byte per character.</summary>
    AnsiString,
}

/// <summary>One field as declared: its type, and its array lengths outermost first (empty for a
/// single value).</summary>
public sealed record FieldDeclaration(
    string Name,
    TypeReference Type,
    IReadOnlyList<long> Lengths,
    FieldStorage Storage,
    SourcePosition Position);

/// <summary>One struct as declared, its fields in declaration order.</summary>
public sealed record StructDeclaration(string Name, IReadOnlyList<FieldDeclaration> Fields, SourcePosition Position);

/// <summary>
/// What a pasted definition declares: its structs, the type aliases they may use (C's
/// <c>typedef</c>), and the problems met reading it. A problem does not stop the rest being read:
/// every struct that could be read is here, and one that uses something unreadable fails only
/// when it is laid out.
/// </summary>
public sealed class StructDefinition
{
    private readonly Dictionary<string, StructDeclaration> byName = new(StringComparer.Ordinal);

    internal StructDefinition(
        StructLanguage language,
        IReadOnlyList<StructDeclaration> declarations,
        IReadOnlyDictionary<string, TypeReference> aliases,
        IReadOnlyList<StructProblem> problems)
    {
        Language = language;
        Declarations = declarations;
        Aliases = aliases;
        Problems = problems;
        foreach (var declaration in declarations)
            this.byName.TryAdd(declaration.Name, declaration);
    }

    public StructLanguage Language { get; }

    /// <summary>The structs declared, in the order they appear.</summary>
    public IReadOnlyList<StructDeclaration> Declarations { get; }

    /// <summary>The type aliases declared, by name.</summary>
    public IReadOnlyDictionary<string, TypeReference> Aliases { get; }

    public IReadOnlyList<StructProblem> Problems { get; }

    /// <summary>The struct named <paramref name="name"/>, or null when nothing of that name is
    /// declared.</summary>
    public StructDeclaration? Find(string name) => this.byName.GetValueOrDefault(name);
}
