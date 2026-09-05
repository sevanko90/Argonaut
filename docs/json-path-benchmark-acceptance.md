# JSONPath decoded-name acceptance — 2026-09-05

The change is accepted against the following criteria:

- Ordinary-name sibling lookup takes at most 1.10 times the previous implementation's time.
- Sibling lookup eliminates per-property string allocations.
- Escaped names resolve and selected paths round-trip correctly, including Unicode surrogate pairs, quotes and backslashes.
- Decoding uses constant scratch space; no decoded names are retained in the index and no decoding is added to indexing.

## Measurement

Run from the repository root:

```sh
dotnet run -c Release --project Argonaut.Tests -- --filter '*JsonPathNameBenchmarks*'
dotnet test Argonaut.Tests/Argonaut.Tests.csproj -c Release
```

BenchmarkDotNet 0.14.0 ShortRun, Apple M5 / macOS ARM64, .NET 10.0.5. Each operation walks 10,000 properties of an already indexed object to find its final property. The baseline reproduces the old sibling loop, allocating a UTF-16 string for every name. For escaped names the baseline looks up the serialized spelling; it does not provide the new logical-name semantics.

| Names | Previous mean | Decoded mean | Time ratio | Previous allocated | Decoded allocated |
|---|---:|---:|---:|---:|---:|
| Ordinary | 85.04 µs | 65.84 µs | 0.77 | 399,200 B | 0 B |
| Escaped | 98.51 µs | 144.82 µs | 1.47 | 479,920 B | 0 B |

Ordinary-name acceptance passes. Escaped-name correctness costs roughly 46 µs per 10,000 names relative to the previous raw-text comparison, while eliminating its allocations. These are local short-run measurements, not cross-platform latency guarantees. An earlier run measured 0.72x ordinary-name and 1.52x escaped-name time.

The benchmark isolates the sibling loop: it excludes parsing the requested path, encoding each requested member once, thread-pool scheduling and UI updates. It does not measure first-paint latency or large-file resident memory. Source inspection establishes that this decoded-name change adds no indexing work or persistent index fields. Lookup uses 12 bytes of decoding scratch space; selected-path decoding counts characters and writes directly into the final string, without a name-sized temporary buffer. Tests also check zero scratch allocations when decoding a 600 KB escaped name.

UI teardown is covered separately by queued-synchronization-context tests. Mapping readers start through `StartDependentRead`; disposal joins background work only. UI continuations are awaited separately, preserving cancellation → completion → unmap ordering without joining UI continuations from the UI thread.
