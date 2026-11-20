using Argonaut.Features.Hex.Structs;

namespace Argonaut.Tests.Features.Hex.Structs;

public sealed class StructParserTests
{
    [Theory]
    [InlineData("struct A { int x; };", StructLanguage.C)]
    [InlineData("typedef struct { uint8_t a; } A;", StructLanguage.C)]
    [InlineData("#pragma once\nstruct A { unsigned short x; char name[4]; };", StructLanguage.C)]
    [InlineData("struct A { public: int x; private: char y; };", StructLanguage.C)]
    [InlineData("public struct A { public int X; }", StructLanguage.CSharp)]
    [InlineData("[StructLayout(LayoutKind.Sequential)]\nstruct A { int X; }", StructLanguage.CSharp)]
    [InlineData("unsafe struct A { fixed byte Name[16]; }", StructLanguage.CSharp)]
    [InlineData("struct A { uint X; ushort Y; }", StructLanguage.CSharp)]
    [InlineData("record struct P(int X, int Y);", StructLanguage.CSharp)]
    public void TheLanguage_IsToldFromTheSource(string source, StructLanguage expected)
        => Assert.Equal(expected, StructParser.Detect(source));

    [Fact]
    public void CommentsAndOtherDirectives_AreIgnored()
    {
        var definition = StructParser.Parse("""
            #include <stdint.h>
            #pragma once
            // struct NotThis { int x; };
            /* struct NorThis { int y; }; */
            struct A { int x; /* inline */ char y; // trailing
            };
            """);

        Assert.Empty(definition.Problems);
        Assert.Equal(["A"], definition.Declarations.Select(d => d.Name));
        Assert.Equal(["x", "y"], definition.Declarations[0].Fields.Select(f => f.Name));
    }

    [Fact]
    public void Lengths_ComeFromDefinesAndConstants_WithArithmetic()
    {
        var definition = StructParser.Parse("""
            #define BASE 0x10
            #define TWICE (BASE << 1)
            enum { Ignored = 3 };
            constexpr int Third = 010;
            struct A { char a[BASE]; char b[TWICE + 1]; char c[Third * 2 - 1]; char d[1'000]; };
            """);

        Assert.Empty(definition.Problems);
        Assert.Equal([16L, 33L, 15L, 1000L], definition.Declarations[0].Fields.Select(f => f.Lengths.Single()));
    }

    [Fact]
    public void CSharpConstants_SizeFixedBuffers()
    {
        var definition = StructParser.Parse("""
            unsafe struct A
            {
                public const int Length = 8;
                public fixed byte Name[Length * 2];
            }
            """);

        Assert.Equal(16, definition.Declarations[0].Fields.Single().Lengths.Single());
    }

    [Theory]
    [InlineData("struct A { virtual void f(); int x; };", "vtable")]
    [InlineData("struct A { int n; char data[]; };", "needs a length")]
    [InlineData("struct A { char data[COUNT]; };", "not a constant")]
    [InlineData("struct A : B, C { int x; };", "single inheritance")]
    [InlineData("struct A : virtual B { int x; };", "Virtual inheritance")]
    [InlineData("struct alignas(3) A { int x; };", "power of two")]
    public void WhatCannotBeLaidOut_IsReported_AtItsPlace(string source, string expected)
    {
        var problem = Assert.Single(StructParser.Parse(source, StructLanguage.C).Problems);
        Assert.Contains(expected, problem.Message);
        Assert.Equal(1, problem.Position.Line);
    }

    [Theory]
    [InlineData("struct A { public string Name; }", "ByValTStr")]
    [InlineData("struct A { public int[] Values; }", "ByValArray")]
    [InlineData("struct A { public int? Maybe; }", "Nullable")]
    [InlineData("struct A<T> { public T Value; }", "Generic")]
    public void CSharpFieldsStoredElsewhere_AreReported(string source, string expected)
    {
        var problem = Assert.Single(StructParser.Parse(source, StructLanguage.CSharp).Problems);
        Assert.Contains(expected, problem.Message);
    }

    [Theory]
    [InlineData("struct A { public char[20] Name; public int After; }")]
    [InlineData("struct A { public char[20] Name { get; set; } public int After; }")]
    [InlineData("struct A { public char Name[20]; public int After; }")]
    public void ACSharpCharArrayWithALength_IsStoredInPlace_TwoBytesACharacter(string source)
    {
        var definition = StructParser.Parse(source, StructLanguage.CSharp);
        Assert.Empty(definition.Problems);

        var laid = StructLayoutPass.Lay(definition, "A", StructTarget.Lp64).Struct!;
        Assert.Equal(["Name", "After"], laid.Fields.Select(f => f.Name));
        Assert.Equal(40, laid.Fields[0].Size);
        Assert.Equal(40, laid.Fields[1].Offset);
    }

    [Fact]
    public void ACSharpMemberThatCannotBeRead_IsReported_NotDropped()
    {
        var problem = Assert.Single(StructParser.Parse("struct A { public int; public int B; }", StructLanguage.CSharp).Problems);
        Assert.Contains("Expected a field name", problem.Message);
    }

    [Fact]
    public void AnUnknownType_FailsOnlyTheStructThatUsesIt()
    {
        var definition = StructParser.Parse("struct Good { int x; }; struct Bad { int x; Mystery m; };");

        Assert.NotNull(StructLayoutPass.Lay(definition, "Good", StructTarget.Lp64).Struct);
        var bad = StructLayoutPass.Lay(definition, "Bad", StructTarget.Lp64);
        Assert.Null(bad.Struct);
        Assert.Contains("unknown type Mystery", bad.Problem!.Message);
        Assert.Equal(new SourcePosition(1, 53), bad.Problem.Position);
    }

    [Fact]
    public void AStructHoldingItself_IsReported_AndAPointerToItIsFine()
    {
        var definition = StructParser.Parse("struct Node { int value; struct Node* next; }; struct Loop { int x; Loop inner; };");

        var node = StructLayoutPass.Lay(definition, "Node", StructTarget.Lp64).Struct!;
        Assert.Equal(StructScalarKind.Pointer, node.Fields[1].Scalar!.Kind);
        Assert.Contains("contains itself", StructLayoutPass.Lay(definition, "Loop", StructTarget.Lp64).Problem!.Message);
    }

    [Fact]
    public void Typedefs_AliasTypesAndPointers()
    {
        var definition = StructParser.Parse("""
            typedef unsigned long ULONG;
            typedef struct _ITEM { int id; } ITEM, *PITEM;
            using Count = unsigned short;
            struct A { ULONG size; PITEM item; ITEM inline_item; Count n; };
            """);

        var laid = StructLayoutPass.Lay(definition, "A", StructTarget.Llp64).Struct!;
        Assert.Equal(4, laid.Fields[0].Size);
        Assert.Equal(StructScalarKind.Pointer, laid.Fields[1].Scalar!.Kind);
        Assert.Equal("_ITEM", laid.Fields[2].Struct!.Name);
        Assert.Equal(2, laid.Fields[3].Size);
    }

    [Fact]
    public void PackedAfterTheBrace_PacksTheStruct_AsCCompilersDo()
    {
        // Clang ignores this form in C++ but packs in C, and GCC packs in both, so it packs here;
        // it is kept out of the C++ fixture for that reason.
        var definition = StructParser.Parse("struct A { char c; int i; } __attribute__((packed));");
        var laid = StructLayoutPass.Lay(definition, "A", StructTarget.Lp64).Struct!;

        Assert.Equal(5, laid.Size);
        Assert.Equal(1, laid.Fields[1].Offset);
    }

    [Fact]
    public void ABitfield_KnowsItsBits_WithinTheBytesItSpans()
    {
        var definition = StructParser.Parse("struct A { unsigned a : 3; unsigned b : 7; unsigned c : 22; };");
        var laid = StructLayoutPass.Lay(definition, "A", StructTarget.Lp64).Struct!;
        var b = laid.Fields[1];

        Assert.Equal((0L, 3, 7, 2L), (b.Offset, b.BitOffset, b.BitWidth!.Value, b.Size));
        Assert.Equal(10, laid.Fields[2].BitPosition);
        Assert.Empty(laid.Padding);
    }

    [Fact]
    public void AUnion_PutsEveryFieldAtItsStart_AndIsAsBigAsItsLargest()
    {
        var definition = StructParser.Parse("union U { char c; double d; int i[3]; };");
        var laid = StructLayoutPass.Lay(definition, "U", StructTarget.Lp64).Struct!;

        Assert.Equal(StructKind.Union, laid.Kind);
        Assert.All(laid.Fields, f => Assert.Equal(0, f.Offset));
        Assert.Equal(16, laid.Size);
        Assert.Equal([new StructPadding(12, 4)], laid.Padding);
    }

    [Fact]
    public void AnExplicitFieldWithoutAnOffset_IsReported()
    {
        var definition = StructParser.Parse("[StructLayout(LayoutKind.Explicit)] struct A { [FieldOffset(0)] public int X; public int Y; }");
        Assert.Contains("needs a [FieldOffset]", StructLayoutPass.Lay(definition, "A", StructTarget.Lp64).Problem!.Message);
    }

    [Fact]
    public void AutoLayout_IsReported()
        => Assert.Contains("LayoutKind.Auto", Assert.Single(StructParser.Parse("[StructLayout(LayoutKind.Auto)] struct A { public int X; }").Problems).Message);

    [Fact]
    public void ALongDouble_IsRefused_ByName()
    {
        var definition = StructParser.Parse("struct A { long double x; };");
        Assert.Contains("long double", StructLayoutPass.Lay(definition, "A", StructTarget.Lp64).Problem!.Message);
    }

    [Fact]
    public void ANameNotDeclared_SaysSo()
        => Assert.Contains("No struct named", StructLayoutPass.Lay(StructParser.Parse("struct A { int x; };"), "B", StructTarget.Lp64).Problem!.Message);
}
