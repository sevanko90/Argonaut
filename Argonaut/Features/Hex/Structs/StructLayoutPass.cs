using System;
using System.Collections.Generic;

namespace Argonaut.Features.Hex.Structs;

/// <summary>How a primitive's bytes are read.</summary>
public enum StructScalarKind
{
    Signed,
    Unsigned,
    Float,
    Bool,

    /// <summary>A character, its size naming its encoding: 1 byte, 2 (UTF-16) or 4 (UTF-32).</summary>
    Character,

    /// <summary>An address, shown and never followed.</summary>
    Pointer,
}

/// <summary>A primitive type as stored: what it is called in the definition's language, how its
/// bytes read, and its size and alignment.</summary>
public sealed record StructScalar(string Name, StructScalarKind Kind, int Size, int Alignment);

/// <summary>Bytes the layout adds between fields, or after the last, so each field and the struct
/// as a whole sit at their alignment.</summary>
public readonly record struct StructPadding(long Offset, long Size);

/// <summary>
/// A field laid out: where it starts in its struct, and what it holds - a primitive or a nested
/// struct, as one value or an array. An array is arithmetic: element <c>i</c> (counted across
/// every dimension) starts at <c>Offset + i × Stride</c>.
/// </summary>
public sealed record LaidField(
    string Name,
    string TypeName,
    long Offset,
    StructScalar? Scalar,
    LaidStruct? Struct,
    IReadOnlyList<long> Lengths,
    long Stride,
    long Size,
    SourcePosition Position)
{
    public bool IsArray => Lengths.Count > 0;
}

/// <summary>A struct laid out: its fields at their offsets, the padding between them, its size
/// (including trailing padding) and its alignment.</summary>
public sealed record LaidStruct(
    string Name,
    long Size,
    int Alignment,
    IReadOnlyList<LaidField> Fields,
    IReadOnlyList<StructPadding> Padding);

/// <summary>A struct laid out, or why it could not be.</summary>
public sealed record StructLayoutResult(LaidStruct? Struct, StructProblem? Problem);

/// <summary>
/// Lays a declared struct out under its language's rules and a target: each field aligned to its
/// type's alignment, the struct to its widest member, with trailing padding to that alignment, as
/// C, C++ and C#'s sequential layout all do without packing. Never touches the document.
/// </summary>
public static class StructLayoutPass
{
    /// <summary>The largest struct laid out. Anything bigger is surely a mistyped length.</summary>
    private const long MaximumSize = 1L << 40;

    public static StructLayoutResult Lay(StructDefinition definition, string name, StructTarget target)
    {
        if (definition.Find(name) is not { } declaration)
            return new StructLayoutResult(null, new StructProblem($"No struct named {name} is declared.", default));

        var pass = new Pass(definition, target);
        try
        {
            return new StructLayoutResult(pass.Lay(declaration), null);
        }
        catch (LayoutFailed failed)
        {
            return new StructLayoutResult(null, failed.Problem);
        }
    }

    /// <summary>The primitive <paramref name="name"/> names in <paramref name="language"/>, or
    /// null when it is not one.</summary>
    public static StructScalar? Primitive(string name, StructLanguage language, StructTarget target)
        => language == StructLanguage.CSharp ? CSharpPrimitive(name, target) : CPrimitive(name, target);

    private static StructScalar Scalar(string name, StructScalarKind kind, int size) => new(name, kind, size, size);

    private static StructScalar? CPrimitive(string name, StructTarget target) => name switch
    {
        "char" => Scalar(name, StructScalarKind.Character, 1),
        "signed char" or "int8_t" => Scalar(name, StructScalarKind.Signed, 1),
        "unsigned char" or "uint8_t" or "char8_t" or "byte" => Scalar(name, StructScalarKind.Unsigned, 1),
        "short" or "int16_t" => Scalar(name, StructScalarKind.Signed, 2),
        "unsigned short" or "uint16_t" => Scalar(name, StructScalarKind.Unsigned, 2),
        "int" or "int32_t" => Scalar(name, StructScalarKind.Signed, 4),
        "unsigned int" or "uint32_t" => Scalar(name, StructScalarKind.Unsigned, 4),
        "long" => Scalar(name, StructScalarKind.Signed, target.LongSize),
        "unsigned long" => Scalar(name, StructScalarKind.Unsigned, target.LongSize),
        "long long" or "int64_t" => Scalar(name, StructScalarKind.Signed, 8),
        "unsigned long long" or "uint64_t" => Scalar(name, StructScalarKind.Unsigned, 8),
        "float" => Scalar(name, StructScalarKind.Float, 4),
        "double" => Scalar(name, StructScalarKind.Float, 8),
        "bool" => Scalar(name, StructScalarKind.Bool, 1),
        "wchar_t" => Scalar(name, StructScalarKind.Character, target.WideCharSize),
        "char16_t" => Scalar(name, StructScalarKind.Character, 2),
        "char32_t" => Scalar(name, StructScalarKind.Character, 4),
        "size_t" or "uintptr_t" => Scalar(name, StructScalarKind.Unsigned, target.PointerSize),
        "ssize_t" or "intptr_t" or "ptrdiff_t" => Scalar(name, StructScalarKind.Signed, target.PointerSize),
        _ => null,
    };

    private static StructScalar? CSharpPrimitive(string name, StructTarget target) => name switch
    {
        "byte" => Scalar(name, StructScalarKind.Unsigned, 1),
        "sbyte" => Scalar(name, StructScalarKind.Signed, 1),
        "short" => Scalar(name, StructScalarKind.Signed, 2),
        "ushort" => Scalar(name, StructScalarKind.Unsigned, 2),
        "int" => Scalar(name, StructScalarKind.Signed, 4),
        "uint" => Scalar(name, StructScalarKind.Unsigned, 4),
        "long" => Scalar(name, StructScalarKind.Signed, 8),
        "ulong" => Scalar(name, StructScalarKind.Unsigned, 8),
        "Half" => Scalar(name, StructScalarKind.Float, 2),
        "float" => Scalar(name, StructScalarKind.Float, 4),
        "double" => Scalar(name, StructScalarKind.Float, 8),
        "bool" => Scalar(name, StructScalarKind.Bool, 1),
        "char" => Scalar(name, StructScalarKind.Character, 2),
        "nint" => Scalar(name, StructScalarKind.Signed, target.PointerSize),
        "nuint" => Scalar(name, StructScalarKind.Unsigned, target.PointerSize),
        _ => null,
    };

    private sealed class LayoutFailed(StructProblem problem) : Exception(problem.Message)
    {
        public StructProblem Problem { get; } = problem;
    }

    /// <summary>One layout: the structs laid so far, so a struct used twice is laid once, and the
    /// ones in progress, so a struct that holds itself is caught rather than recursed into.</summary>
    private sealed class Pass(StructDefinition definition, StructTarget target)
    {
        private readonly Dictionary<string, LaidStruct> laid = new(StringComparer.Ordinal);
        private readonly HashSet<string> inProgress = new(StringComparer.Ordinal);

        public LaidStruct Lay(StructDeclaration declaration)
        {
            if (this.laid.TryGetValue(declaration.Name, out var done))
                return done;

            if (!this.inProgress.Add(declaration.Name))
                throw new LayoutFailed(new StructProblem($"{declaration.Name} contains itself, so it has no size - use a pointer.", declaration.Position));

            var fields = new List<LaidField>(declaration.Fields.Count);
            var padding = new List<StructPadding>();
            long offset = 0;
            int alignment = 1;
            foreach (var field in declaration.Fields)
            {
                var (scalar, nested, typeName) = Resolve(field, declaration);
                long elementSize = scalar?.Size ?? nested!.Size;
                int elementAlignment = scalar?.Alignment ?? nested!.Alignment;

                long aligned = AlignUp(offset, elementAlignment);
                if (aligned > offset)
                    padding.Add(new StructPadding(offset, aligned - offset));

                long count = 1;
                foreach (long length in field.Lengths)
                    count = checked(count * length);
                long size = checked(elementSize * count);
                if (aligned + size > MaximumSize)
                    throw new LayoutFailed(new StructProblem($"{field.Name} makes {declaration.Name} larger than a struct can be read as.", field.Position));

                fields.Add(new LaidField(field.Name, typeName, aligned, scalar, nested, field.Lengths, elementSize, size, field.Position));
                offset = aligned + size;
                alignment = Math.Max(alignment, elementAlignment);
            }

            // An empty struct still takes a byte, in C++ and C#, so two never share an address.
            long total = Math.Max(1, AlignUp(offset, alignment));
            if (total > offset)
                padding.Add(new StructPadding(offset, total - offset));

            var result = new LaidStruct(declaration.Name, total, alignment, fields, padding);
            this.inProgress.Remove(declaration.Name);
            this.laid[declaration.Name] = result;
            return result;
        }

        private (StructScalar? Scalar, LaidStruct? Nested, string TypeName) Resolve(FieldDeclaration field, StructDeclaration owner)
        {
            var type = field.Type;
            for (int hops = 0; hops < 32 && type.PointerDepth == 0 && definition.Aliases.TryGetValue(type.Name, out var aliased); hops++)
                type = aliased;
            string typeName = field.Type.ToString();

            if (type.PointerDepth > 0 || (definition.Aliases.TryGetValue(type.Name, out var pointerAlias) && pointerAlias.PointerDepth > 0))
                return (new StructScalar(typeName, StructScalarKind.Pointer, target.PointerSize, target.PointerSize), null, typeName);

            switch (field.Storage)
            {
                case FieldStorage.FourByteBool:
                    return (new StructScalar("bool", StructScalarKind.Bool, 4, 4), null, typeName);
                case FieldStorage.OneByteBool:
                    return (new StructScalar("bool", StructScalarKind.Bool, 1, 1), null, typeName);
                case FieldStorage.AnsiString:
                    return (new StructScalar("char", StructScalarKind.Character, 1, 1), null, typeName);
            }

            if (Primitive(type.Name, definition.Language, target) is { } scalar)
                return (scalar, null, typeName);

            if (definition.Find(type.Name) is { } nested)
                return (null, Lay(nested), typeName);

            string hint = type.Name switch
            {
                "long double" => " - its size differs between compilers and it is not read.",
                "void" => " - only a pointer to it has a size.",
                _ => " - declare it in the definition, or check its spelling.",
            };
            throw new LayoutFailed(new StructProblem($"{owner.Name}.{field.Name}: unknown type {type.Name}{hint}", field.Position));
        }

        private static long AlignUp(long offset, int alignment) => (offset + alignment - 1) / alignment * alignment;
    }
}
