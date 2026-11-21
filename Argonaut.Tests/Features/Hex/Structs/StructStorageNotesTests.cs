using Argonaut.Features.Hex.Structs;
using Argonaut.Features.Hex.Values;

namespace Argonaut.Tests.Features.Hex.Structs;

public sealed class StructStorageNotesTests
{
    /// <summary>The note on the field <paramref name="field"/> of <paramref name="name"/>.</summary>
    private static string Note(string source, string name, string field, StructTarget? target = null, ByteOrder order = ByteOrder.Little)
    {
        target ??= StructTarget.Lp64;
        var definition = StructParser.Parse(source);
        var laid = StructLayoutPass.Lay(definition, name, target);
        Assert.True(laid.Struct is not null, laid.Problem?.Message);
        var nodes = new StructNodes(laid.Struct!, 1);
        for (long i = 0; i < nodes.ChildCount(nodes.Root); i++)
        {
            var node = nodes.Child(nodes.Root, i);
            if (node.DisplayLabel == field)
                return StructStorageNotes.For(node, definition.Language, target, order);
        }

        throw new InvalidOperationException($"No row {field}.");
    }

    [Fact]
    public void ACSharpBool_IsOneByte_NotABit()
    {
        string note = Note("struct S { public bool Enabled; }", "S", "Enabled");
        Assert.StartsWith("bool (C#): 1 byte, 0 = false, otherwise true", note);
        Assert.EndsWith("1 byte at +0", note);
    }

    [Fact]
    public void ABoolMarshalledAsBool_SaysItIsFourBytes()
    {
        string note = Note("struct S { [MarshalAs(UnmanagedType.Bool)] public bool B; }", "S", "B");
        Assert.StartsWith("bool (C#): 4 bytes, marshalled as Win32 BOOL", note);
    }

    [Fact]
    public void ACSharpCharArray_SaysWhyItIsTwiceTheLength()
    {
        string note = Note("unsafe struct S { public fixed char Name[256]; }", "S", "Name");
        Assert.StartsWith("char[256]: 256 × 2 bytes = 512 bytes", note);
        Assert.Contains("char (C#): 2 bytes, a UTF-16 code unit", note);
    }

    [Fact]
    public void ACLong_NamesTheTarget_AndWhatItIsOnTheOther()
    {
        Assert.StartsWith("long (C, LLP64): 4 bytes, signed", Note("struct S { long n; };", "S", "n", StructTarget.Llp64));
        Assert.StartsWith("long (C, LP64): 8 bytes, signed", Note("struct S { long n; };", "S", "n", StructTarget.Lp64));
    }

    [Fact]
    public void AMultiByteNumber_SaysWhichByteOrderItIsReadIn()
    {
        Assert.Contains("Read big-endian", Note("struct S { int n; };", "S", "n", order: ByteOrder.Big));
        Assert.DoesNotContain("endian", Note("struct S { char c; };", "S", "c"));
    }

    [Fact]
    public void Padding_SaysWhyItIsThere()
        => Assert.StartsWith("Padding: 3 bytes", Note("struct S { char c; int n; };", "S", "padding"));

    [Fact]
    public void ABitfield_SaysWhichBitsOfWhichUnit()
    {
        string note = Note("struct S { unsigned int a : 3; unsigned int b : 5; };", "S", "b");
        Assert.StartsWith("unsigned int : 5 (C): 5 bits of a 4-byte storage unit, starting at bit 3", note);
        Assert.EndsWith("at +0, bit 3", note);
    }

    [Fact]
    public void ANestedStruct_SaysItsSizeAlignmentAndTrailingPadding()
        => Assert.StartsWith("struct In: 8 bytes, align 4. 3 bytes of it trailing padding",
            Note("struct In { int a; char b; }; struct Out { In inner; };", "Out", "inner"));
}
