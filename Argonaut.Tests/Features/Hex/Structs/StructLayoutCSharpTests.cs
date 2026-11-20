using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Argonaut.Features.Hex.Structs;

namespace Argonaut.Tests.Features.Hex.Structs;

/// <summary>C# layouts against the runtime's: every struct in Fixtures/CSharpLayouts.cs is both
/// compiled into these tests and parsed from its text, and the decoder's offsets must be where the
/// runtime puts the fields - in memory, or as marshalled for the <c>[MarshalAs]</c> fixtures.</summary>
public sealed class StructLayoutCSharpTests
{
    private const string InMemoryNamespace = "Argonaut.Tests.Features.Hex.Structs.Fixtures.InMemory";
    private const string MarshalledNamespace = "Argonaut.Tests.Features.Hex.Structs.Fixtures.Marshalled";

    private static readonly Lazy<StructDefinition> Definition = new(() => StructParser.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Features", "Hex", "Structs", "Fixtures", "CSharpLayouts.cs"))));

    private static IEnumerable<Type> FixtureTypes(string ns)
        => typeof(StructLayoutCSharpTests).Assembly.GetTypes().Where(t => t.Namespace == ns && t.IsValueType && !t.IsEnum && !t.IsNested);

    [Fact]
    public void TheFixture_ReadsAsCSharp_WithoutProblems()
    {
        Assert.Equal(StructLanguage.CSharp, Definition.Value.Language);
        Assert.Empty(Definition.Value.Problems);
    }

    [Fact]
    public void EveryStruct_LaysOutAsTheRuntimeStoresIt()
    {
        var types = FixtureTypes(InMemoryNamespace).ToList();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var laid = Lay(type.Name);
            var fields = InstanceFields(type);
            Assert.Equal((type.Name, (long)RuntimeSize(type)), (type.Name, laid.Size));
            Assert.Equal(
                fields.Select(f => (f.Name, InMemoryOffset(type, f))).ToArray(),
                fields.Zip(laid.Fields, (f, l) => (f.Name, l.Offset)).ToArray());
            Assert.Equal(fields.Count, laid.Fields.Count);
        }
    }

    [Fact]
    public void EveryMarshalledStruct_LaysOutAsTheMarshallerWritesIt()
    {
        var types = FixtureTypes(MarshalledNamespace).ToList();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var laid = Lay(type.Name);
            var fields = InstanceFields(type);
            Assert.Equal((type.Name, (long)Marshal.SizeOf(type)), (type.Name, laid.Size));
            Assert.Equal(
                fields.Select(f => (f.Name, (long)Marshal.OffsetOf(type, f.Name))).ToArray(),
                fields.Zip(laid.Fields, (f, l) => (f.Name, l.Offset)).ToArray());
        }
    }

    [Fact]
    public void TheSample_Is268Bytes_WithItsNameAt9()
    {
        var sample = Lay("Sample");
        Assert.Equal(268, sample.Size);
        Assert.Equal(9, sample.Fields.Single(f => f.Name == "Name").Offset);
    }

    [Fact]
    public void AFixedCharBuffer_IsTwoBytesACharacter()
    {
        var name = Lay("FixedChars").Fields.Single(f => f.Name == "Name");
        Assert.Equal(512, name.Size);
        Assert.Equal(2, name.Stride);
    }

    [Fact]
    public void ABoolMarshalledAsBool_IsFourBytes()
    {
        var bools = Lay("WinBools");
        Assert.Equal(4, bools.Fields.Single(f => f.Name == "B").Size);
        Assert.Equal(1, bools.Fields.Single(f => f.Name == "C").Size);
    }

    [Fact]
    public void AnEnumField_KnowsItsEnum_AndIsStoredAsItsUnderlyingType()
    {
        var enums = Lay("Enums");
        var access = enums.Fields.Single(f => f.Name == "Access");

        Assert.Equal("Access", access.Enum!.Name);
        Assert.True(access.Enum.IsFlags);
        Assert.Equal([0L, 1, 2, 3], access.Enum.Members.Select(m => m.Value));
        Assert.Equal(1, enums.Fields.Single(f => f.Name == "S").Size);
        Assert.Equal(8, enums.Fields.Single(f => f.Name == "W").Size);
    }

    private static LaidStruct Lay(string name)
    {
        var result = StructLayoutPass.Lay(Definition.Value, name, StructTarget.ForThisMachine);
        Assert.True(result.Struct is not null, $"{name}: {result.Problem?.Message}");
        return result.Struct!;
    }

    private static List<FieldInfo> InstanceFields(Type type)
        => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.MetadataToken).ToList();

    private static int RuntimeSize(Type type)
        => (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;

    /// <summary>Where the runtime keeps <paramref name="field"/> in memory: the distance from the
    /// struct's address to the field's, taken with <c>ldflda</c> since C# cannot name a field
    /// generically.</summary>
    private static long InMemoryOffset(Type type, FieldInfo field)
    {
        var method = new DynamicMethod("OffsetOf", typeof(long), [type.MakeByRefType()], typeof(StructLayoutCSharpTests).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, field);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ret);
        return (long)method.Invoke(null, [Activator.CreateInstance(type)])!;
    }
}
