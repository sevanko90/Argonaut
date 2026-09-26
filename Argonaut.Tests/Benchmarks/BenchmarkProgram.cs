using BenchmarkDotNet.Running;

namespace Argonaut.Tests.Benchmarks;

// Entry point for `dotnet run -c Release --project Argonaut.Tests -- --filter *`, and for
// `-- --index-memory`, the indexing speed and memory baseline (IndexMemoryBaseline).
// Not used by `dotnet test` - VSTest discovers [Fact]s via the test adapter and never
// calls Main.
public static class BenchmarkProgram
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--index-memory")
            return IndexMemoryBaseline.Run(args[1..]);

        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkProgram).Assembly).Run(args);
        return 0;
    }
}
