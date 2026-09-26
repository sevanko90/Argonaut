# Indexing baseline - 2026-09-26

A point-in-time measurement of indexing speed and memory for each kind of document, to hold
later changes against. Taken on branch `plan/json-sparse-index` at `7b3e5e3`, a Release build.

Re-run it the same way and compare row by row:

```sh
dotnet build -c Release Argonaut.Tests/Argonaut.Tests.csproj
dotnet run -c Release --no-build --project Argonaut.Tests -- --index-memory --size-mib 1024 --out result.md
```

`--size-mib` sets each document's size (default 256). The harness is
`Argonaut.Tests/Benchmarks/IndexMemoryBaseline.cs`: it writes deterministic documents, and runs
each in its own process so the peak working set belongs to that document alone.

## Result

Documents of 1024 MiB each; 3 open-index-release cycles per document in a fresh
process, after a warm-up on a small document of the same kind. Speed and allocation are the
median cycle; peaks are the highest of the cycles.

- Machine: Apple M5, 32 GB, 10 cores
- Runtime: .NET 10.0.5, macOS 27.0.0, Workstation GC

| Document | Indexer | Items | Time | Speed | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,996,610 | 180 ms | 5,701 MiB/s | 2.5 MB | 0.0024 | 0/0/0 | 2.5 MB | 1.05 GB | 2.5 MB | -7.6 KB / -7.1 KB |
| Csv | FileOffsetIndex | 6,414,338 | 135 ms | 7,559 MiB/s | 98.0 MB | 0.0957 | 3/3/3 | 98.1 MB | 1.14 GB | 98.0 MB | 488 B / 952 B |
| NdJson | FileOffsetIndex | 6,262,785 | 132 ms | 7,771 MiB/s | 95.7 MB | 0.0934 | 3/3/3 | 95.7 MB | 1.14 GB | 95.7 MB | 488 B / 952 B |
| JsonRecords | JsonSparseIndex | 16,370 | 1,818 ms | 563 MiB/s | 774.6 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.05 GB | 777.0 KB | 488 B / 952 B |
| JsonTokenDense | JsonSparseIndex | 16,384 | 2,352 ms | 435 MiB/s | 774.3 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.05 GB | 776.8 KB | 488 B / 952 B |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 16,371 | 2,147 ms | 477 MiB/s | 779.0 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 781.5 KB | 488 B / 952 B |

Documents:

- **Text**: log lines, 60-200 bytes, every 5,000th 40 KB. Baseline before indexing: heap 158.8 KB, working set 45.6 MB.
- **Csv**: 12 columns, quoted text with commas and doubled quotes. Baseline before indexing: heap 151.0 KB, working set 46.7 MB.
- **NdJson**: one record per line, nested object and array. Baseline before indexing: heap 151.0 KB, working set 46.7 MB.
- **JsonRecords**: one root array of records (JsonShape.RecordArray). Baseline before indexing: heap 159.2 KB, working set 55.0 MB.
- **JsonTokenDense**: one root array of short numbers (JsonShape.TokenDenseArray). Baseline before indexing: heap 155.2 KB, working set 54.8 MB.
- **JsonRecordsWithHashes**: as JSON records, indexed the way a diff indexes. Baseline before indexing: heap 159.3 KB, working set 55.3 MB.

Columns: *Peak heap* and *Index kept* and *Left after release* are managed heap above the
baseline - kept is after a full collection with the index still open, left after release is
after closing it and collecting again (what a leak would show as). *Peak working set* is the
whole process and includes the file's mapped pages, clean and free to reclaim on macOS.

## Reading it

- **Speed is warm-cache.** Each document is indexed straight after being written, so its pages are
  in the OS page cache; a cold open from disk is bounded by the disk instead. Two runs agreed
  within about 3%.
- **The peak working set is roughly the file** for every kind: the scan touches every byte, so the
  whole mapping becomes resident. Those pages are clean - on macOS the OS drops them for free - and
  the managed heap columns are the app's own memory.
- **Lines cost 16 bytes each.** CSV and NDJSON keep a `FileLineSpan` per line (~96 MB for 6.3M
  lines), which is all of their allocation; the three gen-2 collections are most
  likely that list growing on the large object heap. Halving
  it is on the roadmap.
- **The JSON index is 0.0007 bytes per file byte** - under 1 MB for a gigabyte, whatever the shape.
  Speed is what shape changes: token-dense JSON is the slowest, and content hashes (what a diff
  indexes with) cost about 18% over the plain index. JSON runs its structural scan and validation
  side by side, so its time is the slower of the two.
- **Nothing is left behind.** After release and a full collection the heap is back to its baseline
  within a kilobyte; the few hundred bytes that grow per cycle are the harness's own, identical for
  every indexer. The text case reads slightly negative because its baseline caught some warm-up
  garbage.
- **Against the July profile** (a 1 GB file, dense token index): JSON allocated 2.82 GB then and
  775 KB now; the raw index was ~1 GB/s then, measured differently and on a different machine, so
  compare speeds with this run rather than that one.
