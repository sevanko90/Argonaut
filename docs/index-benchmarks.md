# Indexing benchmarks

What indexing costs for each kind of document - speed, allocation, peak memory, what the finished
index keeps, and whether anything is left behind once it is released - measured run after run, so
an improvement or a regression shows up against what came before. Newest run first.

Small documents are where a fixed per-session cost shows: see
[index-fixed-cost.md](index-fixed-cost.md) for the 1, 16 and 256 MiB runs, what they found and the
fix. A run should record a 1 MiB point as well as 1 GiB, so a fixed cost stays visible.

## Running it

```sh
dotnet build -c Release Argonaut.Tests/Argonaut.Tests.csproj
dotnet run -c Release --no-build --project Argonaut.Tests -- --index-memory --size-mib 1024 --out result.md
```

`--size-mib` sets each document's size (default 256; the runs below use 1024). The harness is
`Argonaut.Tests/Benchmarks/IndexMemoryBaseline.cs`: it writes deterministic documents, and runs
each in fresh processes so the peak working set belongs to that document alone. Each process
indexes a small document of the same kind first, then opens, indexes and releases the real one
three times, and its speed is the median cycle.

A whole process can run slow, so each document runs in three processes (`--processes N` changes
that; 1 gives a quick look) and the table shows the median process with the *Speed range* across
them. A change inside that range is noise. Memory is the same from process to process. A 1024 MiB
run takes about 75 seconds.

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
- **Xml**: one root of CityGML-like records: namespaces, attributes, nested geometry.

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

| Run | Text | Csv | NdJson | JsonRecords | JsonTokenDense | JsonRecordsWithHashes | Xml |
|---|---:|---:|---:|---:|---:|---:|---:|
| 2026-10-28 `c335c87` XML view | 2.5 MB | 259.2 KB | 259.0 KB | 777.0 KB | 776.8 KB | 781.5 KB | 1.6 MB |
| 2026-10-10 `2f5aaeb` UI spike check | 2.5 MB | 259.2 KB | 259.2 KB | 776.8 KB | 776.8 KB | 781.5 KB | - |
| 2026-09-26 `07f5cf5` sparse line index | 2.5 MB | 259.2 KB | 259.0 KB | 776.8 KB | 777.3 KB | 781.6 KB | - |
| 2026-09-26 `7b3e5e3` baseline | 2.5 MB | 98.0 MB | 95.7 MB | 777.0 KB | 776.8 KB | 781.5 KB | - |

Speed:

| Run | Text | Csv | NdJson | JsonRecords | JsonTokenDense | JsonRecordsWithHashes | Xml |
|---|---:|---:|---:|---:|---:|---:|---:|
| 2026-10-28 `c335c87` XML view | 6,302 MiB/s | 9,029 MiB/s | 9,126 MiB/s | 565 MiB/s | 433 MiB/s | 469 MiB/s | 908 MiB/s |
| 2026-10-10 `2f5aaeb` UI spike check | 6,340 MiB/s | 9,086 MiB/s | 9,109 MiB/s | 563 MiB/s | 429 MiB/s | 472 MiB/s | - |
| 2026-09-26 `07f5cf5` sparse line index | 5,864 MiB/s | 8,066 MiB/s | 8,098 MiB/s | 565 MiB/s | 420 MiB/s | 473 MiB/s | - |
| 2026-09-26 `7b3e5e3` baseline | 5,701 MiB/s | 7,559 MiB/s | 7,771 MiB/s | 563 MiB/s | 435 MiB/s | 477 MiB/s | - |

## Runs

### 2026-10-31 - segments sized in bytes (cloud Linux VM, new baseline)

`SegmentedAppendLog<T>` segments went from 8,192 items to about 16 KiB, so an index session's
first append no longer buys up to 384 KiB per log on the large object heap
([index-fixed-cost.md](index-fixed-cost.md)). Measured before and after on a different machine
from every run below, so it is its own baseline and is not in the M5 trend tables.

- Machine: X64, 4 cores (cloud VM)
- Runtime: .NET 10.0.14, Ubuntu 24.04.5 LTS, Workstation GC
- 3 processes per document.

**1 MiB, before** (the fixed cost dominates):

| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,816 | 1 ms | 718 MiB/s | 519-721 | 141.6 KB | 0.1381 | 0/0/0 | 168.8 KB | 39.9 MB | 129.4 KB | 208 B / 440 B |
| Csv | FileOffsetIndex | 8,194 | 1 ms | 1,756 MiB/s | 1,467-2,257 | 138.8 KB | 0.1054 | 0/0/0 | 168.8 KB | 40.2 MB | 129.5 KB | 208 B / 440 B |
| NdJson | FileOffsetIndex | 8,193 | 1 ms | 1,929 MiB/s | 1,686-1,941 | 139.0 KB | 0.1030 | 0/0/0 | 160.8 KB | 40.3 MB | 129.6 KB | 208 B / 440 B |
| JsonRecords | JsonSparseIndex | 16 | 10 ms | 99 MiB/s | 67-117 | 613.9 KB | 0.5995 | 0/0/0 | 648.9 KB | 46.9 MB | 580.4 KB | 208 B / 440 B |
| JsonTokenDense | JsonSparseIndex | 16 | 17 ms | 59 MiB/s | 56-74 | 631.6 KB | 0.6168 | 0/0/0 | 656.9 KB | 46.7 MB | 580.4 KB | 208 B / 440 B |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 17 | 17 ms | 60 MiB/s | 59-81 | 636.0 KB | 0.6211 | 0/0/0 | 665.0 KB | 46.9 MB | 582.8 KB | 208 B / 440 B |
| Xml | XmlSparseIndex | 16 | 13 ms | 78 MiB/s | 57-88 | 901.8 KB | 0.8780 | 0/0/0 | 928.9 KB | 43.8 MB | 834.1 KB | 208 B / 440 B |

**1 MiB, after:**

| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,816 | 2 ms | 622 MiB/s | 617-761 | 29.6 KB | 0.0289 | 0/0/0 | 56.8 KB | 39.9 MB | 17.4 KB | 208 B / 440 B |
| Csv | FileOffsetIndex | 8,194 | 1 ms | 1,728 MiB/s | 1,481-1,787 | 26.8 KB | 0.0203 | 0/0/0 | 48.8 KB | 40.3 MB | 17.6 KB | 208 B / 440 B |
| NdJson | FileOffsetIndex | 8,193 | 1 ms | 1,801 MiB/s | 1,776-1,826 | 26.8 KB | 0.0198 | 0/0/0 | 48.8 KB | 40.5 MB | 17.5 KB | 208 B / 440 B |
| JsonRecords | JsonSparseIndex | 16 | 12 ms | 84 MiB/s | 83-88 | 64.8 KB | 0.0633 | 0/0/0 | 96.9 KB | 46.7 MB | 28.3 KB | 208 B / 440 B |
| JsonTokenDense | JsonSparseIndex | 16 | 19 ms | 52 MiB/s | 48-81 | 85.5 KB | 0.0835 | 0/0/0 | 110.9 KB | 46.5 MB | 28.4 KB | 240 B / 472 B |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 17 | 19 ms | 52 MiB/s | 44-70 | 90.0 KB | 0.0878 | 0/0/0 | 113.0 KB | 46.8 MB | 30.8 KB | 208 B / 440 B |
| Xml | XmlSparseIndex | 16 | 12 ms | 80 MiB/s | 69-85 | 135.9 KB | 0.1323 | 0/0/0 | 169.0 KB | 44.1 MB | 74.3 KB | 208 B / 440 B |

**1 GiB, before:**

| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,996,610 | 409 ms | 2,507 MiB/s | 2,241-2,524 | 3.5 MB | 0.0035 | 0/0/0 | 3.8 MB | 1.05 GB | 2.5 MB | 208 B / 3.4 KB |
| Csv | FileOffsetIndex | 6,414,338 | 306 ms | 3,345 MiB/s | 3,216-3,355 | 1.0 MB | 0.0010 | 0/0/0 | 1.1 MB | 1.04 GB | 257.5 KB | 208 B / 440 B |
| NdJson | FileOffsetIndex | 6,262,785 | 302 ms | 3,396 MiB/s | 3,357-3,413 | 1.0 MB | 0.0010 | 0/0/0 | 1.1 MB | 1.04 GB | 257.4 KB | 208 B / 440 B |
| JsonRecords | JsonSparseIndex | 16,370 | 2,925 ms | 350 MiB/s | 315-356 | 8.0 MB | 0.0078 | 0/0/0 | 9.3 MB | 1.06 GB | 772.5 KB | 208 B / 440 B |
| JsonTokenDense | JsonSparseIndex | 16,384 | 5,524 ms | 185 MiB/s | 179-192 | 14.5 MB | 0.0142 | 0/0/0 | 15.9 MB | 1.06 GB | 772.5 KB | 208 B / 440 B |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 16,371 | 4,901 ms | 209 MiB/s | 207-219 | 13.0 MB | 0.0127 | 0/0/0 | 15.1 MB | 1.06 GB | 777.8 KB | 3.2 KB / 3.4 KB |
| Xml | XmlSparseIndex | 16,302 | 1,872 ms | 547 MiB/s | 497-551 | 6.2 MB | 0.0060 | 0/0/0 | 7.3 MB | 1.05 GB | 1.4 MB | 208 B / 440 B |

**1 GiB, after:**

| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,996,610 | 447 ms | 2,291 MiB/s | 2,240-2,421 | 3.5 MB | 0.0034 | 0/0/0 | 3.8 MB | 1.05 GB | 2.4 MB | 208 B / 440 B |
| Csv | FileOffsetIndex | 6,414,338 | 306 ms | 3,341 MiB/s | 3,316-3,463 | 1.0 MB | 0.0010 | 0/0/0 | 1.1 MB | 1.04 GB | 257.8 KB | 208 B / 440 B |
| NdJson | FileOffsetIndex | 6,262,785 | 310 ms | 3,302 MiB/s | 3,070-3,384 | 1.0 MB | 0.0010 | 0/0/0 | 1.2 MB | 1.04 GB | 260.9 KB | 208 B / 3.4 KB |
| JsonRecords | JsonSparseIndex | 16,370 | 3,355 ms | 305 MiB/s | 283-346 | 8.7 MB | 0.0085 | 0/0/0 | 9.7 MB | 1.06 GB | 409.1 KB | 208 B / 440 B |
| JsonTokenDense | JsonSparseIndex | 16,384 | 4,884 ms | 210 MiB/s | 196-216 | 12.6 MB | 0.0123 | 0/0/0 | 13.7 MB | 1.06 GB | 404.3 KB | 3.2 KB / 3.4 KB |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 16,371 | 4,559 ms | 225 MiB/s | 222-250 | 11.9 MB | 0.0116 | 0/0/0 | 13.7 MB | 1.06 GB | 406.6 KB | 3.2 KB / 3.4 KB |
| Xml | XmlSparseIndex | 16,302 | 1,972 ms | 519 MiB/s | 480-535 | 6.0 MB | 0.0059 | 0/0/0 | 7.0 MB | 1.05 GB | 1.0 MB | 3.2 KB / 3.4 KB |

- **Index kept at 1 MiB: 580 → 28 KB (JSON), 834 → 74 KB (XML), 130 → 18 KB (CSV, NDJSON,
  Text).** At 1 GiB JSON 773 → 409 KB and XML 1.4 → 1.0 MB; CSV, NDJSON and Text unchanged, their
  segments were already full.
- **Speed: within noise**, in both directions.
- This VM allocates more at 1 GiB than the M5 on both sides (transient scan allocation, not the
  index); compare *Index kept* across machines, not *Allocated*.

### 2026-10-28 - XML view (`c335c87`)

The XML tree view's index joins the harness: `XmlSparseIndex` over one root of CityGML-like
records. It indexes and checks the document's structure in one pass at 908 MiB/s, faster than
JSON's sparse index (whose speed is set by its separate validation pass), with allocation that
does not grow with the file. It keeps 1.6 MB per GiB: the sparse containers and checkpoints, plus
the same-name sibling counts snapshotted at each checkpoint for XPath numbering. The other
documents are unchanged within their ranges; step 1 of the XML work moved tree-view code but no
indexer.

| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,996,610 | 163 ms | 6,302 MiB/s | 6,194-6,305 | 2.5 MB | 0.0024 | 0/0/0 | 2.5 MB | 1.05 GB | 2.5 MB | 488 B / 952 B |
| Csv | FileOffsetIndex | 6,414,338 | 113 ms | 9,029 MiB/s | 9,019-9,030 | 267.0 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.2 KB | 488 B / 952 B |
| NdJson | FileOffsetIndex | 6,262,785 | 112 ms | 9,126 MiB/s | 9,111-9,151 | 267.0 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.0 KB | 488 B / 952 B |
| JsonRecords | JsonSparseIndex | 16,370 | 1,812 ms | 565 MiB/s | 560-567 | 774.8 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 777.0 KB | 488 B / 952 B |
| JsonTokenDense | JsonSparseIndex | 16,384 | 2,366 ms | 433 MiB/s | 402-434 | 774.4 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 776.8 KB | 488 B / 952 B |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 16,371 | 2,181 ms | 469 MiB/s | 469-476 | 779.2 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 781.5 KB | 488 B / 952 B |
| Xml | XmlSparseIndex | 16,302 | 1,128 ms | 908 MiB/s | 902-915 | 1.5 MB | 0.0014 | 0/0/0 | 1.5 MB | 1.05 GB | 1.6 MB | 488 B / 952 B |

### 2026-10-10 - UI spike check (`2f5aaeb`)

A check after the UI chrome spike (icons, tree hints, expanders, search pills), which changed
drawing and view models but no indexer.

- Machine: Apple M5, 32 GB, 10 cores
- Runtime: .NET 10.0.5, macOS 27.0.1, Workstation GC
- 3 processes per document.

| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,996,610 | 162 ms | 6,340 MiB/s | 6,195-6,348 | 2.5 MB | 0.0024 | 0/0/0 | 2.5 MB | 1.05 GB | 2.5 MB | 488 B / 952 B |
| Csv | FileOffsetIndex | 6,414,338 | 113 ms | 9,086 MiB/s | 9,023-9,118 | 266.8 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.2 KB | 488 B / 952 B |
| NdJson | FileOffsetIndex | 6,262,785 | 112 ms | 9,109 MiB/s | 9,085-9,192 | 266.8 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.2 KB | 488 B / 952 B |
| JsonRecords | JsonSparseIndex | 16,370 | 1,818 ms | 563 MiB/s | 559-564 | 774.6 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 776.8 KB | 488 B / 952 B |
| JsonTokenDense | JsonSparseIndex | 16,384 | 2,386 ms | 429 MiB/s | 422-429 | 774.3 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 776.8 KB | 488 B / 952 B |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 16,371 | 2,168 ms | 472 MiB/s | 471-484 | 779.0 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 781.5 KB | 488 B / 952 B |

- **Memory: identical.** Allocation, peak heap, index kept and what is left after release match
  the previous run to the byte, give or take a few hundred bytes of noise.
- **JSON speed: unchanged**, inside the previous ranges.
- **Text, CSV and NDJSON read 8-13% faster** with no change to their indexers. The macOS point
  release is the only other difference; treat it as the machine, not the code.

### 2026-09-26 - sparse line index (`07f5cf5`)

The CSV and NDJSON line index went from a span per line to an anchor every 64 KB or 1024 lines.

- Machine: Apple M5, 32 GB, 10 cores
- Runtime: .NET 10.0.5, macOS 27.0.0, Workstation GC
- 3 processes per document.

| Document | Indexer | Items | Time | Speed | Speed range | Allocated | Alloc / byte | GCs (0/1/2) | Peak heap | Peak working set | Index kept | Left after release (1st / last) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Text | RawSegmentIndex (wrap 160) | 9,996,610 | 175 ms | 5,864 MiB/s | 5,786-5,900 | 2.5 MB | 0.0024 | 0/0/0 | 2.5 MB | 1.05 GB | 2.5 MB | 488 B / 952 B |
| Csv | FileOffsetIndex | 6,414,338 | 127 ms | 8,066 MiB/s | 8,030-8,131 | 266.8 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.2 KB | 488 B / 952 B |
| NdJson | FileOffsetIndex | 6,262,785 | 126 ms | 8,098 MiB/s | 8,088-8,137 | 266.8 KB | 0.0003 | 0/0/0 | 296.8 KB | 1.05 GB | 259.0 KB | 488 B / 952 B |
| JsonRecords | JsonSparseIndex | 16,370 | 1,812 ms | 565 MiB/s | 561-566 | 774.6 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 776.8 KB | 488 B / 952 B |
| JsonTokenDense | JsonSparseIndex | 16,384 | 2,440 ms | 420 MiB/s | 419-434 | 774.6 KB | 0.0007 | 0/0/0 | 816.7 KB | 1.05 GB | 777.3 KB | 488 B / 2.7 KB |
| JsonRecordsWithHashes | JsonSparseIndex + content hashes | 16,371 | 2,165 ms | 473 MiB/s | 464-475 | 779.0 KB | 0.0007 | 0/0/0 | 800.8 KB | 1.06 GB | 781.6 KB | 488 B / 952 B |

- **CSV and NDJSON: 98 MB → 259 KB kept**, allocation down by the same, and the three gen-2
  collections are gone. Speed reads about 6% up, but the baseline was a single process with no
  range, so treat it as unchanged. *Items* is still the line count.
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
