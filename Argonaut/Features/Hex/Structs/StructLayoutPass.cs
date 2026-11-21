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
/// A field laid out: where it starts in its struct, and what it holds - a primitive, an enum or a
/// nested struct, as one value or an array. An array is arithmetic: element <c>i</c> (counted
/// across every dimension) starts at <c>Offset + i × Stride</c>. A bitfield holds
/// <see cref="BitWidth"/> bits starting <see cref="BitOffset"/> bits into the byte at
/// <see cref="Offset"/>, spanning <see cref="Size"/> bytes.
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

    /// <summary>The enum a field's integer is named by, if its type is one.</summary>
    public EnumDeclaration? Enum { get; init; }

    /// <summary>A bitfield's first bit within the byte at <see cref="Offset"/>, 0 the lowest.</summary>
    public int BitOffset { get; init; }

    /// <summary>A bitfield's width in bits; null for an ordinary field.</summary>
    public int? BitWidth { get; init; }

    /// <summary>Whether this is the base struct a C++ struct derives from, rather than a field.</summary>
    public bool IsBase { get; init; }

    /// <summary>Where the field starts, in bits from the struct's start.</summary>
    public long BitPosition => Offset * 8 + BitOffset;
}

/// <summary>A struct or union laid out: its fields at their offsets, the padding between them,
/// its size (including trailing padding) and its alignment.</summary>
public sealed record LaidStruct(
    string Name,
    long Size,
    int Alignment,
    IReadOnlyList<LaidField> Fields,
    IReadOnlyList<StructPadding> Padding)
{
    public StructKind Kind { get; init; } = StructKind.Struct;

    /// <summary>Where its last field ends, before the trailing padding.</summary>
    public long DataSize { get; init; }

    /// <summary>Whether C++ counts it as plain old data, which decides whether a struct deriving
    /// from it may use its tail padding.</summary>
    public bool IsPlainData { get; init; } = true;

    /// <summary>The language it was declared in, which says how its types are stored.</summary>
    public StructLanguage Language { get; init; }
}

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

            var result = new Placement(this, declaration).Place();
            this.inProgress.Remove(declaration.Name);
            this.laid[declaration.Name] = result;
            return result;
        }

        public StructTarget Target => target;

        public StructLanguage Language => definition.Language;

        /// <summary>What a field holds, its type's size and natural alignment, and the name to
        /// show for its type.</summary>
        public readonly record struct Element(StructScalar? Scalar, LaidStruct? Struct, EnumDeclaration? Enum, string TypeName)
        {
            public long Size => Scalar?.Size ?? Struct!.Size;

            public int Alignment => Scalar?.Alignment ?? Struct!.Alignment;
        }

        public Element Resolve(FieldDeclaration field, StructDeclaration owner)
            => Resolve(field.Type, field.Storage, owner.HasWideStrings, $"{owner.Name}.{(field.Name.Length > 0 ? field.Name : "(unnamed)")}", field.Position);

        public Element Resolve(TypeReference written, FieldStorage storage, bool wideStrings, string what, SourcePosition position)
        {
            var type = written;
            for (int hops = 0; hops < 32 && type.PointerDepth == 0 && definition.Aliases.TryGetValue(type.Name, out var aliased); hops++)
                type = aliased;
            string typeName = written.ToString();

            if (type.PointerDepth > 0)
                return new Element(new StructScalar(typeName, StructScalarKind.Pointer, target.PointerSize, target.PointerSize), null, null, typeName);

            switch (storage)
            {
                case FieldStorage.FourByteBool:
                    return new Element(new StructScalar("bool", StructScalarKind.Bool, 4, 4), null, null, typeName);
                case FieldStorage.OneByteBool:
                    return new Element(new StructScalar("bool", StructScalarKind.Bool, 1, 1), null, null, typeName);
                case FieldStorage.AnsiString:
                    int width = wideStrings ? 2 : 1;
                    return new Element(new StructScalar("char", StructScalarKind.Character, width, width), null, null, typeName);
            }

            if (Primitive(type.Name, definition.Language, target) is { } scalar)
                return new Element(scalar, null, null, typeName);

            if (definition.FindEnum(type.Name) is { } enumeration)
            {
                var underlying = Resolve(enumeration.Underlying, FieldStorage.Natural, false, $"{enumeration.Name}'s underlying type", enumeration.Position);
                if (underlying.Scalar is not { Kind: StructScalarKind.Signed or StructScalarKind.Unsigned or StructScalarKind.Character or StructScalarKind.Bool })
                    throw new LayoutFailed(new StructProblem($"{enumeration.Name} must be stored as an integer type.", enumeration.Position));
                return new Element(underlying.Scalar, null, enumeration, typeName);
            }

            if (definition.Find(type.Name) is { } nested)
                return new Element(null, Lay(nested), null, typeName);

            string hint = type.Name switch
            {
                "long double" => " - its size differs between compilers and it is not read.",
                "void" => " - only a pointer to it has a size.",
                _ => " - declare it in the definition, or check its spelling.",
            };
            throw new LayoutFailed(new StructProblem($"{what}: unknown type {type.Name}{hint}", position));
        }
    }

    /// <summary>
    /// Places one struct's fields. Positions are kept in bits so bitfields and ordinary fields share
    /// one cursor: an ordinary field starts at the next byte, aligned to its type's alignment
    /// limited by the pack; a bitfield follows the target's rule for sharing storage units.
    /// </summary>
    private sealed class Placement(Pass pass, StructDeclaration declaration)
    {
        private readonly List<LaidField> fields = new(declaration.Fields.Count + 1);
        private readonly int pack = declaration.Pack ?? int.MaxValue;
        private long cursor;
        private long end;
        private int alignment = 1;

        // Whether the field before was a bitfield, which decides what an MSVC zero-width one does.
        private bool followsBitfield;

        // The storage unit an MSVC bitfield run is in, in bits; open while bitfields continue it.
        private bool unitOpen;
        private long unitStart;
        private long unitBits;

        public LaidStruct Place()
        {
            if (declaration.Base is { } baseType)
                PlaceBase(baseType);

            foreach (var field in declaration.Fields)
            {
                var element = pass.Resolve(field, declaration);
                int fieldAlignment = Math.Min(element.Alignment, this.pack);
                if (field.Alignment is { } asked)
                    fieldAlignment = Math.Max(fieldAlignment, asked);

                if (field.BitWidth is { } width)
                    PlaceBitfield(field, element, fieldAlignment, width);
                else
                    PlaceField(field, element, fieldAlignment);
            }

            CloseUnit();
            if (declaration.Alignment is { } structAlignment)
                this.alignment = Math.Max(this.alignment, structAlignment);

            long dataEnd = Math.Max(this.end, (this.cursor + 7) / 8);
            long total = Math.Max(1, AlignUp(dataEnd, this.alignment));
            if (declaration.MinimumSize is { } minimum)
                total = Math.Max(total, minimum);
            if (total > MaximumSize)
                throw new LayoutFailed(new StructProblem($"{declaration.Name} is larger than a struct can be read as.", declaration.Position));

            return new LaidStruct(declaration.Name, total, this.alignment, this.fields, Gaps(total))
            {
                Kind = declaration.Kind,
                DataSize = dataEnd,
                IsPlainData = declaration.IsPlainData,
                Language = pass.Language,
            };
        }

        /// <summary>The base comes first. An empty base takes no room, as both ABIs lay it out, and
        /// with GCC and Clang the fields after a base that is not plain data start in its tail
        /// padding.</summary>
        private void PlaceBase(TypeReference baseType)
        {
            var element = pass.Resolve(baseType, FieldStorage.Natural, false, $"{declaration.Name}'s base", declaration.Position);
            if (element.Struct is not { } laidBase)
                throw new LayoutFailed(new StructProblem($"{declaration.Name} can only derive from a struct.", declaration.Position));

            bool empty = laidBase.Fields.Count == 0;
            this.fields.Add(new LaidField(laidBase.Name, laidBase.Name, 0, null, laidBase, [], laidBase.Size, empty ? 0 : laidBase.Size, declaration.Position) { IsBase = true });
            this.alignment = Math.Max(this.alignment, Math.Min(laidBase.Alignment, this.pack));
            if (!empty)
                this.cursor = (laidBase.IsPlainData || pass.Target.MsvcBitfields ? laidBase.Size : laidBase.DataSize) * 8;
        }

        private void PlaceField(FieldDeclaration field, Pass.Element element, int fieldAlignment)
        {
            CloseUnit();
            this.followsBitfield = false;
            long count = 1;
            foreach (long length in field.Lengths)
                count = checked(count * length);
            long size = checked(element.Size * count);

            long offset;
            if (declaration.Kind == StructKind.Union)
            {
                offset = 0;
            }
            else if (declaration.IsExplicit)
            {
                offset = field.ExplicitOffset
                    ?? throw new LayoutFailed(new StructProblem($"{declaration.Name}.{field.Name} needs a [FieldOffset] in an explicit layout.", field.Position));
            }
            else
            {
                offset = AlignUp((this.cursor + 7) / 8, fieldAlignment);
                this.cursor = (offset + size) * 8;
            }

            if (offset + size > MaximumSize)
                throw new LayoutFailed(new StructProblem($"{field.Name} makes {declaration.Name} larger than a struct can be read as.", field.Position));

            this.end = Math.Max(this.end, offset + size);
            this.alignment = Math.Max(this.alignment, fieldAlignment);
            this.fields.Add(new LaidField(field.Name, element.TypeName, offset, element.Scalar, element.Struct, field.Lengths, element.Size, size, field.Position)
            {
                Enum = element.Enum,
            });
        }

        private void PlaceBitfield(FieldDeclaration field, Pass.Element element, int fieldAlignment, int width)
        {
            if (element.Scalar is not { Kind: StructScalarKind.Signed or StructScalarKind.Unsigned or StructScalarKind.Bool or StructScalarKind.Character } scalar)
                throw new LayoutFailed(new StructProblem($"{declaration.Name}.{field.Name}: a bitfield must be an integer type.", field.Position));
            if (field.Lengths.Count > 0)
                throw new LayoutFailed(new StructProblem($"{declaration.Name}.{field.Name}: a bitfield cannot be an array.", field.Position));

            long typeBits = scalar.Size * 8L;
            if (width > typeBits)
                throw new LayoutFailed(new StructProblem($"{declaration.Name}.{field.Name}: {width} bits do not fit in a {scalar.Name}.", field.Position));

            long bit;
            if (declaration.Kind == StructKind.Union)
            {
                bit = 0;
            }
            else if (pass.Target.MsvcBitfields)
            {
                bit = PlaceMsvcBits(width, typeBits, fieldAlignment);
            }
            else
            {
                bit = PlaceItaniumBits(width, typeBits, fieldAlignment, scalar.Alignment);
            }

            // GCC and Clang let an unnamed bitfield pad without aligning the struct; MSVC aligns it
            // to every bitfield's type, except a zero-width one that follows no bitfield.
            if (field.Name.Length > 0 || (pass.Target.MsvcBitfields && (width > 0 || this.followsBitfield)))
                this.alignment = Math.Max(this.alignment, fieldAlignment);
            this.followsBitfield = true;

            long first = bit / 8;
            long size = width == 0 ? 0 : (bit + width + 7) / 8 - first;
            if (declaration.Kind == StructKind.Union)
                this.end = Math.Max(this.end, (width + 7) / 8);
            this.fields.Add(new LaidField(field.Name, element.TypeName, first, scalar, null, [], scalar.Size, size, field.Position)
            {
                Enum = element.Enum,
                BitOffset = (int)(bit % 8),
                BitWidth = width,
            });
        }

        /// <summary>GCC and Clang: a bitfield goes at the next free bit unless it would cross a
        /// boundary of its type's alignment, when it moves to that boundary; packed, it never moves.
        /// A zero-width one moves the next field to its type's boundary.</summary>
        private long PlaceItaniumBits(int width, long typeBits, int fieldAlignment, int naturalAlignment)
        {
            long boundary = fieldAlignment * 8L;
            if (width == 0)
            {
                this.cursor = AlignUp(this.cursor, naturalAlignment * 8L);
                return this.cursor;
            }

            bool packed = fieldAlignment < naturalAlignment;
            if (!packed && this.cursor / typeBits != (this.cursor + width - 1) / typeBits)
                this.cursor = AlignUp(this.cursor, boundary);

            long bit = this.cursor;
            this.cursor += width;
            return bit;
        }

        /// <summary>MSVC: bitfields share a storage unit the size of their type while the type's
        /// size stays the same and they fit; otherwise a new unit starts at the type's alignment.
        /// A zero-width one closes the unit.</summary>
        private long PlaceMsvcBits(int width, long typeBits, int fieldAlignment)
        {
            if (width == 0)
            {
                // After a bitfield it ends the unit and moves on to its type's boundary; after
                // anything else it does nothing.
                CloseUnit();
                if (this.followsBitfield)
                    this.cursor = AlignUp(this.cursor, fieldAlignment * 8L);
                return this.cursor;
            }

            if (!this.unitOpen || this.unitBits != typeBits || this.cursor + width > this.unitStart + this.unitBits)
            {
                CloseUnit();
                this.unitStart = AlignUp(this.cursor, fieldAlignment * 8L);
                this.unitBits = typeBits;
                this.unitOpen = true;
                this.cursor = this.unitStart;
            }

            long bit = this.cursor;
            this.cursor += width;
            return bit;
        }

        private void CloseUnit()
        {
            if (!this.unitOpen)
                return;

            this.cursor = this.unitStart + this.unitBits;
            this.unitOpen = false;
        }

        /// <summary>The bytes no field covers, up to <paramref name="total"/>: the padding.</summary>
        private List<StructPadding> Gaps(long total)
        {
            var covered = new List<(long Start, long End)>(this.fields.Count);
            foreach (var field in this.fields)
            {
                if (field.Size > 0)
                    covered.Add((field.Offset, field.Offset + field.Size));
            }

            covered.Sort();
            var gaps = new List<StructPadding>();
            long reached = 0;
            foreach (var (start, stop) in covered)
            {
                if (start > reached)
                    gaps.Add(new StructPadding(reached, start - reached));
                reached = Math.Max(reached, stop);
            }

            if (total > reached)
                gaps.Add(new StructPadding(reached, total - reached));
            return gaps;
        }

        private static long AlignUp(long offset, long alignment) => (offset + alignment - 1) / alignment * alignment;
    }
}
