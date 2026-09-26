# Indexing benchmarks

What indexing costs for each kind of document - speed, allocation, peak memory, what the finished
index keeps, and whether anything is left behind once it is released - measured run after run, so
an improvement or a regression shows up against what came before. Newest run first.

## Running it

```sh
dotnet build -c Release Argonaut.Tests/Argonaut.Tests.csproj
dotnet run -c Release --no-build --project Argonaut.Tests -- --index-memory --size-mib 1024 --out result.md
```

`--size-mib` sets each document's size (default 256; the runs below use 1024). The harness is
`Argonaut.Tests/Benchmarks/IndexMemoryBaseline.cs`: it writes deterministic documents, and runs
each in its own process so the peak working set belongs to that document alone. Each process
indexes a small document of the same kind first, then opens, indexes and releases the real one
three times; speed and allocation are the median cycle, peaks the highest.

A single run can be off - one run of the sparse-line-index change had JSON 3-16% slow, and the
next two matched the baseline. So take three runs and record the median one.

To add a run: a new section at the top of [Runs](#runs) with the date, the commit, what changed
and the harness's table, and a row at the top of each table in [Trend](#trend). Compare on the
same machine; a new machine starts a new baseline.

## Documents

- **Text**: log lines, 60-200 bytes, every 5,000th 40 KB.
- **Csv**: 12 columns, quoted text with commas and doubled quotes.
- **NdJson**: one record per line, nested object and array.
- **JsonRecords**: one root array of records (JsonShape.RecordArray).
- **JsonTokenDense**: one root array of short numbers (JsonShape.TokenDenseArray).
- **JsonRecordsWithHashes**: as JSON records, indexed the way a diff indexes.

## Columns

*Peak heap*, *Index kept* and *Left after release* are managed heap above the baseline taken
before indexing - kept is after a full collection with the index still open, left after release
is after closing it and collecting again (what a leak would show as). *Peak working set* is the
whole process and includes the file's mapped pages, clean and free to reclaim on macOS.

Speed is warm-cache: each document is indexed straight after being written, so its pages are in
the OS page cache; a cold open from disk is bounded by the disk instead. The peak working set is
roughly the file for every kind, since the scan touches every byte - the managed heap columns are
the app's own memory.

## Trend

1 GiB documents, Apple M5.

Index kept:

| Run | Text | Csv | NdJson | JsonRecords | JsonTokenDense | JsonRecordsWithHashes |
|---|---:|---:|---:|---:|---:|---:|
| 2026-09-26 `07f5cf5` sparse line index | 2.5 MB | 259.2 KB | 259.2 KB | 777.0 KB | 777.0 KB | 781.5 KB |
| 2026-09-26 `7b3e5e3` baseline | 2.5 MB | 98.0 MB | 95.7 MB | 777.0 KB | 776.8 KB | 781.5 KB |

Speed:

| Run | Text | Csv | NdJson | JsonRecords | JsonTokenDense | JsonRecordsWithHashes |
|---|---:|---:|---:|---:|---:|---:|
| 2026-09-26 `07f5cf5` sparse line index | 5,857 MiB/s | 7,826 MiB/s | 8,031 MiB/s | 560 MiB/s | 423 MiB/s | 468 MiB/s |
| 2026-09-26 `7b3e5e3` baseline | 5,701 MiB/s | 7,559 MiB/s | 7,771 MiB/s | 563 MiB/s | 435 MiB/s | 477 MiB/s |

## Runs

### 2026-09-26 - sparse line index (`07f5cf5`)

The CSV and NDJSON line index went from a span per line to an anchor every 64 KB or 1024 lines.

- Machine: Apple M5, 32 GB, 10 cores
- Runtime: .NET 10.0.5, macOS 27.0.0, Workstation GC
- Median of three runs.

| Document | Indexer | Items | Time | Speed | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,996,610 | 175 ms | 5,857 MiB/s | 2.5 MB | 0.0024 | 0/0/0 | 2.5 MB | 1.05 GB | 2.5 MB | 488 B / 952 B |
| Csv | FileOffsetIndex | 6,414,338 | 131 ms | 7,826 MiB/s | 266.8 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.2 KB | 488 B / 952 B |
| NdJson | FileOffsetIndex | 6,262,785 | 128 ms | 8,031 MiB/s | 266.8 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.2 KB | 488 B / 952 B |
| JsonRecords | JsonSparseIndex | 16,370 | 1,829 ms | 560 MiB/s | 773.7 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 777.0 KB | 488 B / 952 B |
| JsonTokenDense | JsonSparseIndex | 16,384 | 2,423 ms | 423 MiB/s | 773.7 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.05 GB | 777.0 KB | 488 B / 952 B |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 16,371 | 2,188 ms | 468 MiB/s | 779.0 KB | 0.0007 | 0/0/0 | 808.4 KB | 1.06 GB | 781.5 KB | 488 B / 952 B |

- **CSV and NDJSON: 98 MB → 259 KB kept**, allocation down by the same, and the three gen-2
  collections are gone. Speed is unchanged within run-to-run noise. *Items* is still the line
  count.
- **Peak working set 1.14 GB → 1.05 GB** for CSV and NDJSON: the per-line list was the only thing
  above the mapped file.
- Text and JSON: unchanged within noise.

### 2026-09-26 - baseline (`7b3e5e3`)

The first run of the harness, after the JSON view moved to the sparse index.

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

- **Lines cost 16 bytes each.** CSV and NDJSON kept a `FileLineSpan` per line (~96 MB for 6.3M
  lines), which was all of their allocation; the three gen-2 collections were most likely that
  list growing on the large object heap.
- **The JSON index is 0.0007 bytes per file byte** - under 1 MB for a gigabyte, whatever the
  shape. Speed is what shape changes: token-dense JSON is the slowest, and content hashes (what a
  diff indexes with) cost about 18% over the plain index. JSON runs its structural scan and
  validation side by side, so its time is the slower of the two.
- **Nothing is left behind.** After release and a full collection the heap is back to its baseline
  within a kilobyte; the few hundred bytes that grow per cycle are the harness's own, identical
  for every indexer. The text case reads slightly negative because its baseline caught some
  warm-up garbage.
- **Against the July profile** (a 1 GB file, dense token index): JSON allocated 2.82 GB then and
  775 KB now; the raw index was ~1 GB/s then, measured differently and on a different machine.
