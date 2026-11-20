using Argonaut.Features.Hex.Structs;

namespace Argonaut.Tests.Features.Hex.Structs;

/// <summary>C layouts against Clang's: every struct in Fixtures/c-layouts.h, laid out for both
/// targets, must match the size, alignment and field offsets (in bits, so bitfields are exact)
/// record-c-layouts.sh recorded.</summary>
public sealed class StructLayoutCTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Features", "Hex", "Structs", "Fixtures", name);

    public static TheoryData<string> Targets => new() { "lp64", "llp64" };

    [Theory]
    [MemberData(nameof(Targets))]
    public void EveryStruct_LaysOutAsClangDoes(string targetName)
    {
        var target = targetName == "lp64" ? StructTarget.Lp64 : StructTarget.Llp64;
        var definition = StructParser.Parse(File.ReadAllText(Fixture("c-layouts.h")), StructLanguage.C);
        Assert.Empty(definition.Problems);

        var expectations = File.ReadAllLines(Fixture($"c-layouts.{targetName}.txt")).Where(l => l.Length > 0).ToList();
        Assert.NotEmpty(expectations);
        var mismatches = new List<string>();
        foreach (string line in expectations)
        {
            var parts = line.Split(' ');
            string name = parts[0].Split("::")[^1];
            var result = StructLayoutPass.Lay(definition, name, target);
            if (result.Struct is not { } laid)
            {
                mismatches.Add($"{name}: {result.Problem?.Message}");
                continue;
            }

            // Clang's "<name> <size> <alignment> <offsets in bits>", which the decoder must match.
            string ours = string.Join(' ', new[] { parts[0], laid.Size.ToString(), laid.Alignment.ToString() }
                .Concat(laid.Fields.Where(f => !f.IsBase).Select(f => f.BitPosition.ToString())));
            if (ours != line)
                mismatches.Add($"expected {line}\n     got {ours}");
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void TheSample_Is268Bytes_WithItsNameAt9()
    {
        var definition = StructParser.Parse(File.ReadAllText(Fixture("c-layouts.h")));
        var sample = StructLayoutPass.Lay(definition, "Sample", StructTarget.Lp64).Struct!;

        Assert.Equal(268, sample.Size);
        Assert.Equal(9, sample.Fields.Single(f => f.Name == "name").Offset);
        Assert.Equal([new StructPadding(265, 3)], sample.Padding);
    }

    [Fact]
    public void AnArray_IsArithmetic_ItsStrideTheElementsSize()
    {
        var definition = StructParser.Parse(File.ReadAllText(Fixture("c-layouts.h")));
        var arrays = StructLayoutPass.Lay(definition, "Arrays", StructTarget.Lp64).Struct!;
        var grid = arrays.Fields.Single(f => f.Name == "grid");

        Assert.Equal([4L, 3L], grid.Lengths);
        Assert.Equal(4, grid.Stride);
        Assert.Equal(48, grid.Size);
        Assert.Equal(33, arrays.Fields.Single(f => f.Name == "wide").Lengths.Single());
    }

    [Fact]
    public void ANestedStruct_IsLaidOutAsItsOwnType()
    {
        var definition = StructParser.Parse(File.ReadAllText(Fixture("c-layouts.h")));
        var nested = StructLayoutPass.Lay(definition, "Nested", StructTarget.Lp64).Struct!;
        var basic = nested.Fields.Single(f => f.Name == "basic");

        Assert.Equal("Basic", basic.Struct!.Name);
        Assert.Equal(24, basic.Struct.Size);
        Assert.Equal("Tail", nested.Fields.Single(f => f.Name == "tail").Struct!.Name);
    }
}
