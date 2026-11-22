using System.Buffers.Binary;
using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex.Structs;
using Argonaut.Features.Hex.Values;

namespace Argonaut.Tests.Features.Hex.Structs;

public sealed class StructJsonWriterTests
{
    private static StructTree Tree(string source, string name, byte[] bytes, long count = 1)
    {
        var definition = StructParser.Parse(source);
        var laid = StructLayoutPass.Lay(definition, name, StructTarget.Lp64);
        Assert.True(laid.Struct is not null, laid.Problem?.Message);
        return new StructTree(new StructNodes(laid.Struct!, count), new MemoryByteSource(bytes), 0, ByteOrder.Little, StructTarget.Lp64);
    }

    private static JsonElement Json(StructTree tree) => JsonDocument.Parse(StructJsonWriter.Write(tree, tree.Nodes.Root).Json).RootElement;

    [Fact]
    public void TheSample_CopiesAsAnObject_OfItsFieldsAsTheirJsonKinds()
    {
        var bytes = new byte[268];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 42);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), 1.5f);
        bytes[8] = 1;
        "Argonaut \"quoted\""u8.CopyTo(bytes.AsSpan(9));

        var json = Json(Tree("struct Sample { int32_t count; float ratio; bool enabled; char name[256]; };", "Sample", bytes));

        Assert.Equal(42, json.GetProperty("count").GetInt32());
        Assert.Equal(1.5, json.GetProperty("ratio").GetDouble());
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("Argonaut \"quoted\"", json.GetProperty("name").GetString());
        Assert.Equal(4, json.EnumerateObject().Count());
    }

    [Fact]
    public void AnArrayOfStructs_IsAnArray_AndNumbersArraysAreArrays()
    {
        var bytes = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var json = Json(Tree("struct P { uint16_t v[2]; };", "P", bytes, count: 2));

        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal([3, 4], json[1].GetProperty("v").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void EnumsAreNames_PointersStrings_AndBytesPastTheEndNull()
    {
        // mode at 0, the pointer aligned to 8, and beyond at 16 - past these 16 bytes.
        var bytes = new byte[16];
        bytes[0] = 2;
        bytes[8] = 0xAB;
        var json = Json(Tree("enum Mode { Off, On, Auto }; struct S { enum Mode mode; void* p; int beyond; };", "S", bytes));

        Assert.Equal("Auto", json.GetProperty("mode").GetString());
        Assert.Equal("0x00000000000000AB", json.GetProperty("p").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("beyond").ValueKind);
    }

    [Fact]
    public void AnonymousMembersAndBases_AreTheStructsOwnFields_AndPaddingIsLeftOut()
    {
        var json = Json(Tree("struct B { char b; }; struct S : B { union { int i; float f; }; char c; };", "S", new byte[16]));
        Assert.Equal(["b", "i", "f", "c"], json.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void AHugeArray_StopsAtTheLimit_AndSaysSo()
    {
        var tree = Tree("struct B { uint8_t b; };", "B", new byte[StructJsonWriter.MaxValues + 10], count: StructJsonWriter.MaxValues + 10);
        var result = StructJsonWriter.Write(tree, tree.Nodes.Root);

        Assert.True(result.Truncated);
        JsonDocument.Parse(result.Json).Dispose();
    }
}
