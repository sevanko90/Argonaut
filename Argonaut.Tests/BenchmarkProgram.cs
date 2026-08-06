using BenchmarkDotNet.Running;

namespace Argonaut.Tests;

// Entry point for `dotnet run -c Release --project Argonaut.Tests -- --filter *`.
// Not used by `dotnet test` - VSTest discovers [Fact]s via the test adapter and never
// calls Main.
public static class BenchmarkProgram
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(BenchmarkProgram).Assembly).Run(args);
}
