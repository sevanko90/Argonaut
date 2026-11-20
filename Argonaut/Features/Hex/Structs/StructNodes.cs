using System;
using System.Collections.Generic;

namespace Argonaut.Features.Hex.Structs;

/// <summary>What a struct tree row is.</summary>
public enum StructNodeKind : byte
{
    /// <summary>The mapped struct, or the array of them a long selection reads as.</summary>
    Root,

    /// <summary>A field of a struct.</summary>
    Field,

    /// <summary>One element of an array.</summary>
    Element,

    /// <summary>Bytes the layout adds between fields or after the last.</summary>
    Padding,
}

/// <summary>
/// What a node holds: a struct, a primitive (perhaps named by an enum, perhaps a bitfield), or an
/// array of either. An array keeps all its dimensions and which one this node is at, so
/// <c>int grid[4][3]</c> is an array of 4 arrays of 3 without either being materialised.
/// </summary>
public readonly record struct StructValue(
    LaidStruct? Struct,
    StructScalar? Scalar,
    EnumDeclaration? Enum,
    IReadOnlyList<long> Lengths,
    int Dimension,
    long ElementSize,
    int BitOffset,
    int? BitWidth,
    string TypeName)
{
    public bool IsArray => Dimension < Lengths.Count;

    public bool IsContainer => IsArray || Struct is not null;

    /// <summary>How many elements this dimension has.</summary>
    public long Count => IsArray ? Lengths[Dimension] : 0;

    /// <summary>The bytes one element of this dimension takes.</summary>
    public long Stride
    {
        get
        {
            long stride = ElementSize;
            for (int d = Dimension + 1; d < Lengths.Count; d++)
                stride *= Lengths[d];
            return stride;
        }
    }

    /// <summary>The bytes the whole value takes.</summary>
    public long Size => IsArray ? Count * Stride : ElementSize;

    /// <summary>What one element of this dimension holds.</summary>
    public StructValue ElementValue => this with { Dimension = Dimension + 1 };

    public static StructValue Of(LaidField field) => new(
        field.Struct, field.Scalar, field.Enum, field.Lengths, 0, field.Stride, field.BitOffset, field.BitWidth, field.TypeName);

    public static StructValue Of(LaidStruct laid) => new(laid, null, null, [], 0, laid.Size, 0, null, laid.Name);
}

/// <summary>
/// One row of the struct tree. <see cref="Position"/> is the row's place in the tree with
/// everything expanded - its key, unique and in row order however much is collapsed -
/// and <see cref="Rows"/> how many rows it and everything under it take, so a sibling's
/// position is arithmetic. <see cref="Offset"/> is where its bytes start, from the mapping's
/// start.
/// </summary>
public readonly record struct StructNode(
    StructNodeKind Kind,
    string Label,
    StructValue Value,
    long Offset,
    long Position,
    long Rows,
    long Ordinal)
{
    public bool IsContainer => Kind != StructNodeKind.Padding && Value.IsContainer;

    /// <summary>For a padding row, how many bytes it is.</summary>
    public long Size => Kind == StructNodeKind.Padding ? Value.ElementSize : Value.Size;

    /// <summary>Whether the field is a member without a name - an anonymous struct or union, or
    /// an unnamed bitfield.</summary>
    public bool IsAnonymous => Kind == StructNodeKind.Field && Label.Length == 0;

    /// <summary>Whether the row is the base a C++ struct derives from.</summary>
    public bool IsBase { get; init; }
}

/// <summary>
/// The rows of a mapped struct, worked out rather than stored: a struct's children are its
/// fields and padding in offset order, an array's are its elements, and every child's position
/// and offset follow from its parent's by arithmetic. An array of a million structs costs one
/// node until it is opened, and the rows on screen after that.
/// </summary>
public sealed class StructNodes
{
    /// <summary>A struct's children, laid out once: each field or padding gap with the rows
    /// before it.</summary>
    private sealed record Items(StructNode[] Children, long Rows);

    private readonly Dictionary<LaidStruct, Items> items = new(ReferenceEqualityComparer.Instance);

    /// <param name="laid">The struct mapped.</param>
    /// <param name="count">How many of it, one after another: 1 reads one struct, more an array.</param>
    public StructNodes(LaidStruct laid, long count)
    {
        Struct = laid;
        Count = Math.Max(1, count);
        var value = Count == 1
            ? StructValue.Of(laid)
            : new StructValue(laid, null, null, [Count], 0, laid.Size, 0, null, laid.Name);
        Root = new StructNode(StructNodeKind.Root, laid.Name, value, 0, 0, RowsOf(value), 0);
    }

    public LaidStruct Struct { get; }

    public long Count { get; }

    public StructNode Root { get; }

    /// <summary>How many bytes the mapping covers.</summary>
    public long Size => Root.Value.Size;

    /// <summary>How many children <paramref name="node"/> has.</summary>
    public long ChildCount(in StructNode node)
    {
        if (!node.IsContainer)
            return 0;
        return node.Value.IsArray ? node.Value.Count : ItemsOf(node.Value.Struct!).Children.Length;
    }

    /// <summary><paramref name="node"/>'s child at <paramref name="ordinal"/>.</summary>
    public StructNode Child(in StructNode node, long ordinal)
    {
        var value = node.Value;
        if (value.IsArray)
        {
            var element = value.ElementValue;
            long rows = RowsOf(element);
            return new StructNode(StructNodeKind.Element, $"[{ordinal}]", element, node.Offset + ordinal * value.Stride,
                node.Position + 1 + ordinal * rows, rows, ordinal);
        }

        var child = ItemsOf(value.Struct!).Children[ordinal];
        return child with { Offset = node.Offset + child.Offset, Position = node.Position + child.Position };
    }

    /// <summary>The child of <paramref name="node"/> whose rows hold <paramref name="position"/>,
    /// which must be inside the node's rows and not the node's own.</summary>
    public StructNode ChildHolding(in StructNode node, long position)
    {
        long within = position - node.Position - 1;
        if (node.Value.IsArray)
            return Child(node, Math.Clamp(within / RowsOf(node.Value.ElementValue), 0, node.Value.Count - 1));

        var children = ItemsOf(node.Value.Struct!).Children;
        int low = 0, high = children.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (children[middle].Position - 1 <= within)
                low = middle;
            else
                high = middle - 1;
        }

        return Child(node, low);
    }

    /// <summary>The first child of <paramref name="node"/> whose bytes hold
    /// <paramref name="offset"/>, or null.</summary>
    public StructNode? ChildAtOffset(in StructNode node, long offset)
    {
        if (node.Value.IsArray)
        {
            long stride = node.Value.Stride;
            long ordinal = stride == 0 ? 0 : (offset - node.Offset) / stride;
            return ordinal >= 0 && ordinal < node.Value.Count ? Child(node, ordinal) : null;
        }

        long count = ChildCount(node);
        for (long i = 0; i < count; i++)
        {
            var child = Child(node, i);
            if (offset >= child.Offset && offset < child.Offset + Math.Max(1, child.Size))
                return child;
        }

        return null;
    }

    /// <summary>How many rows a value takes with everything under it expanded.</summary>
    public long RowsOf(in StructValue value)
    {
        if (value.IsArray)
            return checked(1 + value.Count * RowsOf(value.ElementValue));
        return value.Struct is { } laid ? 1 + ItemsOf(laid).Rows : 1;
    }

    /// <summary>
    /// A struct's children: its fields in declaration order with each padding gap before the first
    /// field at or after it, positions and offsets relative to the struct. A union's fields all
    /// start at 0, so its padding comes last.
    /// </summary>
    private Items ItemsOf(LaidStruct laid)
    {
        if (this.items.TryGetValue(laid, out var known))
            return known;

        var ordered = new List<(long Offset, bool IsPadding, object Item)>(laid.Fields.Count + laid.Padding.Count);
        foreach (var field in laid.Fields)
            ordered.Add((field.Offset, false, field));

        foreach (var gap in laid.Padding)
        {
            int at = ordered.FindIndex(o => !o.IsPadding && o.Offset >= gap.Offset);
            if (laid.Kind == StructKind.Union || at < 0)
                ordered.Add((gap.Offset, true, gap));
            else
                ordered.Insert(at, (gap.Offset, true, gap));
        }

        var children = new StructNode[ordered.Count];
        long rows = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var (offset, isPadding, item) = ordered[i];
            StructNode child;
            if (isPadding)
            {
                var gap = (StructPadding)item;
                var value = new StructValue(null, null, null, [], 0, gap.Size, 0, null, "padding");
                child = new StructNode(StructNodeKind.Padding, "padding", value, offset, rows + 1, 1, i);
            }
            else
            {
                var field = (LaidField)item;
                var value = StructValue.Of(field);
                child = new StructNode(StructNodeKind.Field, field.Name, value, offset, rows + 1, RowsOf(value), i) { IsBase = field.IsBase };
            }

            children[i] = child;
            rows = checked(rows + child.Rows);
        }

        var result = new Items(children, rows);
        this.items[laid] = result;
        return result;
    }
}
