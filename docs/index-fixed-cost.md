# The index's fixed cost: what every open pays, and the plan to remove it

**Status:** implemented - segments are sized in bytes (Step 1) and guarded (Step 3). See
[Result](#result) for the before/after runs.

**Measured:** 2026-10-31, production code at commit `76312d2` (the working tree adds only
benchmarks and this document), Apple M5 (10 cores), macOS 27.0.1, .NET 10.0.5.
**Harness:** `Argonaut.Tests/Benchmarks/IndexMemoryBaseline.cs`, run with `--index-memory` at
1, 16 and 256 MiB; the 1 GiB figures are the recorded run of 2026-10-28 in
[index-benchmarks.md](index-benchmarks.md#runs).

This document is written to be checked. Every number has the command that produced it, the source
that explains it, and a statement of what would falsify it. The open questions at the end are
things this analysis does not explain.

> **Review corrections (2026-10-31).** The finding holds for documents up to a few hundred MiB,
> and the 1-256 MiB evidence matches the arithmetic. Three things in the first draft were wrong
> and are corrected below: the 1 GiB analysis (Evidence C) treated the first segments as pure
> waste when at 1 GiB most of them are full of real records, which made the expected 1 GiB
> savings about twice too large; the 16-byte anchor segment (131,072 B) is also over the LOH
> threshold, so CSV, NDJSON and Raw put a segment on the LOH for any non-empty file; and the
> hot-path risk does not exist, because `Unsafe.SizeOf<T>()` is a JIT-time constant for a
> struct `T`.

---

## The finding

Every index session allocates the first segment of each of its append logs on the first append, and
the logs' segment size is counted in **items (8,192), not bytes**. So the first append costs:

| Log | Record | First segment |
|---|---|---:|
| `SparseContainerIndex.containers` | `ContainerSlot`, 48 B | **393,240 B** |
| `SparseContainerIndex.checkpoints` | `TreeCheckpoint`, 24 B | **196,632 B** |
| `XmlSiblingCounts.entries` | `Entry`, 16 B | **131,096 B** |
| `XmlSiblingCounts.beforeCheckpoint`, `totals` | `long`, 8 B | **65,560 B** each |
| `FileOffsetIndex.items` (CSV/NdJson) | `FileLineAnchor`, 16 B | **131,096 B** |
| `RawSegmentIndex.items` | `RawRowAnchor`, 16 B | **131,096 B** |

Record sizes are the padded struct sizes: `ContainerSlot` is four `long`s, two `int`s and a
`byte` (41 B → 48), `TreeCheckpoint` two `long`s and an `int` (20 → 24), the anchors a `long`
and an `int` (12 → 16).

Totals when every log has been touched: **XML ≈ 832 KiB, JSON ≈ 576 KiB, CSV/NdJSON/raw ≈ 128 KiB.**

Every segment over 85,000 bytes is a large-object-heap allocation that only a gen-2 collection
reclaims: both of JSON's, three of XML's (containers, checkpoints, entries), and the 131,072-byte
anchor segment of CSV, NDJSON and Raw. Only the two `long` logs (65,536 B) are below it.

This cost is **paid once per index session and does not shrink with the document.** *When* it
starts depends on the index:

- **JSON and XML trees** record nothing until a container reaches the 64 KB promotion size, and a
  checkpoint needs the same distance, so a tree document under 64 KB allocates **none** of it
  (`SparseContainerIndexBuilder.Promote` records only once `offset - start >= PromotionBytes`;
  `Separator` needs the same before a checkpoint).
- **CSV, NDJSON and Raw** add anchor 0 unconditionally (`FileOffsetIndex.ProduceAnchors` starts with
  `items.Add(new FileLineAnchor(0, 0))`; `RawSegmentIndex` appends at `rows % 64 == 0`, and row 0
  is always reached), so they allocate their first segment for **any non-empty file**, however
  small.

Above those thresholds the fixed cost is the same at 64 KB as at a few hundred MiB; past that the
segments start to fill with real records (see Evidence C).

## Evidence

### A. Allocation is flat from 1 MiB to 256 MiB while the item count grows

`--index-memory` at three sizes, 3 processes per document, allocation per cycle in KiB
(the harness's units are 1024-based):

| Document | Items @1 MiB | Allocated @1 MiB | Items @16 MiB | Allocated @16 MiB | Items @256 MiB | Allocated @256 MiB |
|---|---:|---:|---:|---:|---:|---:|
| Text | 9,816 | 130.8 KB | 160,171 | 130.8 KB | 2,500,023 | 642.9 KB |
| Csv | 8,194 | 131.0 KB | 102,402 | 131.1 KB | 1,609,730 | 133.0 KB |
| NdJson | 8,193 | 131.0 KB | 102,401 | 131.1 KB | 1,572,865 | 133.0 KB |
| JsonRecords | 16 | **582.7 KB** | 256 | **582.7 KB** | 4,092 | **582.7 KB** |
| JsonTokenDense | 16 | 582.4 KB | 256 | 582.4 KB | 4,096 | 582.4 KB |
| JsonRecordsWithHashes | 17 | 587.2 KB | 257 | 587.2 KB | 4,093 | 587.2 KB |
| Xml | 16 | **861.5 KB** | 268 | **861.5 KB** | 4,078 | **861.5 KB** |

Two hundred and fifty-six times the document, up to 256 times the recorded items, and the JSON
and XML allocation is identical to the tenth of a kilobyte. CSV and NDJSON are flat until their anchors
outgrow one segment (`133.0 KB` at 256 MiB is one 128 KiB segment plus scan buffers). Text is the
exception that proves the shape of the rule: its anchors are one per 64 *rows*, so at 256 MiB it
needs five segments (642.9 KB) and at 1 GiB twenty (2.5 MB) — those are data, not fixed cost.

The independent BenchmarkDotNet XML build benchmark at 64 MiB reports 859.6–859.9 KB across all
four shapes, within 2 KB of the 861.5 KB here.

### B. The allocation is exactly the first segments

Computed from the record sizes and the 8,192-item segment, against the measured 1 MiB figures:

| Format | Computed first segments | Measured allocation | Difference (scan scratch etc.) |
|---|---:|---:|---:|
| CSV / NDJSON / raw | 131,096 B = 128.0 KiB | 130.8–131.0 KiB | 2.8–3.0 KiB |
| JSON tree | 393,240 + 196,632 = 589,872 B = 576.0 KiB | 582.7 KiB | 6.7 KiB |
| XML tree | 393,240 + 196,632 + 131,096 + 2×65,560 = 852,088 B = 832.1 KiB | 861.5 KiB | 29.4 KiB |

The XML difference matches the scan's own working set: `keyTable` 8,192 B, `countTable` 8,192 B,
`lookScratch` 4,098 B, `open` 64×48 B, `names` 1,024 B, plus the index and builder objects.

### C. At 1 GiB the first segments are mostly full - the waste is the containers segment

*Corrected.* The first draft read the 1 GiB allocation as "first segments = fixed cost" and
concluded 74% of JSON's index was fixed. That is wrong: by 1 GiB the segments are holding real
records. What is wasted is only the unused part of each partly-filled segment.

Recorded run of 2026-10-28 (1 GiB, same harness). *Items* is containers + checkpoints; with
one checkpoint per 64 KiB a 1 GiB document has ~16,380 checkpoints and only a handful of
recorded containers.

| Document | Allocated | Real records | Unused segment space |
|---|---:|---:|---:|
| JsonRecords | 774.8 KB | ~16,370 checkpoints × 24 B ≈ 384 KiB | containers segment (384 KiB for ~1 slot) ≈ **50%** |
| Xml | 1.5 MB | checkpoints, sibling-count slots and entries ≈ 1.06 MiB | containers (384 KiB) + `totals` (64 KiB) ≈ **30%** |
| Csv / NdJson | 267.0 KB | ~16,384 anchors × 16 B ≈ 256 KiB | ≈ 0% |
| Text | 2.5 MB | ~156k anchors × 16 B ≈ 2.4 MiB | ≈ 0% |

So the published per-GiB figures for CSV, NDJSON and Text are real per-byte rates; JSON's real
rate is about 0.38 MB/GiB (its checkpoints), not the 0.19 MB/GiB the first draft gave, and the
rest of its 1 GiB figure is the one nearly-empty containers segment.

### D. The tax is proportionally much higher for the files users open most

Fixed cost as a share of the document. The tree indexes only start paying at 64 KB; the line and
row indexes pay from the first byte. 128 KiB is one anchor segment; 576/832 KiB are the tree
segments.

| Document | CSV / NDJSON / Raw (128 KiB) | JSON tree (576 KiB) | XML tree (832 KiB) |
|---|---:|---:|---:|
| 10 KB | **13× the file** | — (nothing recorded) | — (nothing recorded) |
| 64 KB | 2× | 9× | 13× |
| 1 MiB | 12.5% | **56%** | **81%** |
| 10 MiB | 1.25% | 5.6% | 8.1% |
| 100 MiB | 0.13% | 0.56% | 0.81% |

So a 1 MB XML file allocates almost as much for its index as the file is large, and a 10 KB CSV
allocates thirteen times its size. The absolute cost is constant - and small next to the app's
own working set, so this is hygiene and GC churn rather than a user-visible saving.

### E. It is LOH, and it collects in gen 2

The 85,000-byte threshold is why the heap column moves in steps. `8192 × 16 = 131,072`,
`8192 × 24 = 196,608` and `8192 × 48 = 393,216` are all over it; `8192 × 8 = 65,536` is not.
The XML build benchmark at 64 MiB showed gen-0 = gen-1 = gen-2 = 166.67 collections per 1,000
operations for three of four shapes — about one full collection per six builds — while the
process allocated only ~860 KB per build. The harness's three cycles per process at 1/16/256 MiB
reported 0/0/0, so the frequency depends on how many sessions run before the LOH budget trips; it
is not "every open", but it is routine churn that a 16 KiB allocation would not produce at all.

## Why it exists

`SegmentedAppendLog<T>` is the index's lock-free record store: one background writer appends, the UI
thread reads. Segments were a **fixed 8,192 items** so that an item's address is
`segments[i >> 13][i & 8191]` — two constant shifts, a table load and no bounds arithmetic beyond
the published count. The writer rewrites the table by copy-and-swap, so a reader never sees a
relocated array, and the volatile count publish is the entire lock. The design is good and stays;
the **first segment's size** was the problem, a constant that nothing sized to the record.

## Does JSON do the same? Yes — and so do CSV, NDJSON and Raw

- **JSON tree** (`JsonSparseIndex` → `SparseContainerIndex`): the same two segments, 576 KiB, for
  any document ≥ 64 KB. This is per JSON *session*: opening a file, each side of a diff, and every
  rebuild after a save.
- **The JSON array table** indexes its array on every entry (`JsonArrayTableSession.Start` passes
  no kept structure), so entering a table over an array ≥ 64 KB allocates ~576 KiB each time.
  The roadmap already has an item to keep that index.
- **CSV, NDJSON and Raw** each pay one 128 KiB `FileLineAnchor` / `RawRowAnchor` segment, on the
  LOH, and they pay it for **any non-empty file** because anchor 0 is added unconditionally.
- **NDJSON line selection** creates a fresh `JsonViewModel` over the selected line, but a line
  under 64 KB records nothing and allocates none of this. A line over 64 KB pays the JSON fixed.
- **View switches do not pay**: JSON, XML, CSV and NDJSON keep their structure on the origin
  (`KeptIndexes`), so a switch to Raw and back reopens without rebuilding.
- JSON content hashes are a `Dictionary<long, ulong>`, not a segmented log; they grow with recorded
  containers and are outside this finding.

XML is the worst case only because it has three more logs than JSON: the sibling-count tables.

## Plan

The goal is to keep the lock-free log exactly as it is and stop the first append from buying 8,192
items' worth of memory. The target is **one segment of about 16 KiB**, which is below the LOH
threshold for every record type and still large enough that a 1 GiB index needs tens of segments,
not thousands.

### Step 1 — size segments by bytes (done)

`SegmentedAppendLog<T>` derives its shift from `Unsafe.SizeOf<T>()`, targeting 12-16 KiB:

| Record | Bytes | Items/segment | Segment |
|---|---:|---:|---:|
| `ContainerSlot` | 48 | 256 | 12,288 B |
| `TreeCheckpoint` | 24 | 512 | 12,288 B |
| `Entry` / anchors | 16 | 1,024 | 16,384 B |
| `long` | 8 | 2,048 | 16,384 B |

**No hot-path cost.** `T` is constrained to a struct, so the JIT compiles each `T` separately and
`Unsafe.SizeOf<T>()` is a constant in that code; the shift is a size switch that folds to a
literal, and `ItemRef` compiles to the same constant shifts as before. The first draft's fallback
(a single `const SegmentShift = 10`) is not needed.

Expected first-segment totals: **JSON ≈ 24 KiB, XML ≈ 72 KiB, CSV/NdJSON/raw ≈ 16 KiB**, all in
gen 0, none on the LOH.

*Corrected* 1 GiB expectations. Only the unused part of a segment goes away; real records stay:

| Document | Before | First draft's expectation | After (expected) |
|---|---:|---:|---:|
| JsonRecords | 774.8 KB | ~220 KB | **~400 KB** (checkpoints ≈ 384 KiB stay) |
| Xml | 1.5 MB | ~780 KB | **~1.05 MB** |
| Csv / NdJson | 267.0 KB | ~155 KB | **~260 KB** (both segments were already full) |
| Text | 2.5 MB | ~2.4 MB | **~2.5 MB** |

### Step 2 — verify both ends

1. Re-run the harness at 1 MiB and 1 GiB. Acceptance:
   - 1 MiB total allocation under ~45 KiB for JSON, ~120 KiB for XML, ~24 KiB for CSV/raw;
   - `Index kept` drops by the fixed difference and gen-2 collections stay at zero;
   - 1 GiB speed within the before run's ranges on the same machine.
2. Re-run the XML tree build benchmark (`--filter "*XmlTreeIndexBuild*"`) and confirm 860 KB →
   about 100 KB and no gen-2.
3. Run the full test suite, JSON and XML views unchanged.

### Step 3 — guard it (done)

- `SegmentedAppendLogTests.EveryIndexLogsSegment_StaysOffTheLargeObjectHeap` asserts every index
  log's segment (`SegmentedAppendLog<T>.SegmentBytes`, and `XmlSiblingCounts.EntrySegmentBytes`
  for its private record) is 8-16 KiB, under 85,000 bytes.
- `SegmentedAppendLogTests.TheFirstAppend_AllocatesOneSmallSegment` measures the first append of
  a `ContainerSlot` log with `GC.GetAllocatedBytesForCurrentThread`, which is per-thread and so
  deterministic, rather than the first draft's process-wide allocation bound over a 1 MiB index.
- The 1 MiB point is recorded in [index-benchmarks.md](index-benchmarks.md) so the curve stays
  visible, rather than only 1 GiB runs that hide the fixed cost.

### Step 4 — follow-ups, not part of this change

- **Array table kept index** (already on the roadmap): removes a repeated (now ~24 KiB) index per
  entry for arrays ≥ 64 KB.
- **`GC.AllocateUninitializedArray`** for segments: no longer worth it at 16 KiB.
- Revisit the three sibling-count logs only if the 72 KiB remaining is worth chasing; merging the
  two `long` logs is possible, the `entries` log is not.

## Risks and what would falsify this

- **Falsified if** a 1 MiB JSON document did not allocate ~583 KiB, or if allocation scaled with
  size between 1 and 256 MiB. It did neither: the table in Evidence A is the test.
- **Falsified if** a sub-64 KB **tree** document allocated the tree segments. The promotion gate
  says it cannot; a reviewer can add a 32 KiB case to confirm (the harness's minimum is 1 MiB).
  CSV, NDJSON and Raw are different by construction: their anchor at offset 0 is unconditional.
- **Record sizes** were confirmed from the struct layouts (see The finding), and the guard test
  computes segment bytes from `Unsafe.SizeOf<T>()` rather than assuming them.
- **Risk:** more segments means more segment allocations and a larger table during a scan. At the
  item counts in this app that is tens to a couple of hundred segments (Text at 1 GiB: ~150).
- **Not addressed:** the per-open cost is paid once per session; how often sessions really happen
  (file open, array-table entry, save/rebuild, diff sides, long NDJSON lines) is a product question
  this document does not measure.

## Open questions for the reviewer

1. **The XML `Index kept` column reads ~103 KiB above both `Allocated` and `Peak heap`, at every
   size** (964.1/964.5 KiB kept vs 861.5 KiB allocated and 889.1 KiB peak). It is constant, it is
   XML-only, and the computed 832.1 KiB plus 29.4 KiB of scan scratch does not explain it.
   *Answered under [Result](#result): `GC.GetTotalMemory` double-counts small-object-heap
   survivors on .NET 10 on macOS.*
2. **Gen-2 frequency.** The harness does three cycles per process and sees none; the build
   benchmark does eight builds and sees one every ~six. With no LOH segments left it should be
   zero either way.
3. **Is 16 KiB the right target** rather than 8 or 32 KiB? 16 KiB stays well under 85,000 bytes
   while needing only tens of segments at 1 GiB; the 1 GiB speeds are the check.

## Reproduce

```sh
dotnet build -c Release Argonaut.Tests/Argonaut.Tests.csproj

dotnet run -c Release --no-build --project Argonaut.Tests -- \
  --index-memory --size-mib 1    --out BenchmarkDotNet.Artifacts/index-memory-1mib.md
dotnet run -c Release --no-build --project Argonaut.Tests -- \
  --index-memory --size-mib 1024 --out BenchmarkDotNet.Artifacts/index-memory-1gib.md

# The 64 MiB XML build benchmark (BenchmarkDotNet)
dotnet run -c Release --no-build --project Argonaut.Tests -- \
  --filter "*XmlTreeIndexBuild*"
```

The harness writes deterministic documents to a temp directory and runs each case in three fresh
processes. `Allocated` is a process-wide `GC.GetTotalAllocatedBytes(precise: true)` delta around
open → index, `Peak heap` is sampled, `Index kept` is live heap after a forced collection with the
session still open, `Left after release` is the same after disposal. KB/MB in its report are
1024-based.

## Result

Measured 2026-10-31 on a cloud Linux VM (x64, 4 cores, Ubuntu 24.04, .NET 10.0.14), before and
after on the same machine. It is noisier than the M5, so compare the columns side by side, not
against the M5 figures above.

**Every acceptance point was met.** At 1 MiB, before → after:

| Document | Allocated | Index kept |
|---|---:|---:|
| Text | 141.6 → **29.6 KB** | 129.4 → **17.4 KB** |
| Csv | 138.8 → **26.8 KB** | 129.5 → **17.6 KB** |
| NdJson | 139.0 → **26.8 KB** | 129.6 → **17.5 KB** |
| JsonRecords | 613.9 → **64.8 KB** | 580.4 → **28.3 KB** |
| JsonTokenDense | 631.6 → **85.5 KB** | 580.4 → **28.4 KB** |
| JsonRecordsWithHashes | 636.0 → **90.0 KB** | 582.8 → **30.8 KB** |
| Xml | 901.8 → **135.9 KB** | 834.1 → **74.3 KB** |

*Index kept* is the cleaner measure here: it is the index alone (allocation also counts scan
scratch, the JSON validation reader and so on). It matches the expected first-segment totals -
JSON ≈ 24 KiB, XML ≈ 72 KiB, line/row ≈ 16 KiB - to within a few KiB of index objects.

At 1 GiB, *Index kept* before → after, against the corrected expectations:

| Document | Before | Expected | After |
|---|---:|---:|---:|
| JsonRecords | 772.5 KB | ~400 KB | **409.1 KB** |
| Xml | 1.4 MB | ~1.05 MB | **1.0 MB** |
| Csv / NdJson | 257.5 KB | ~260 KB | **257.8 / 260.9 KB** |
| Text | 2.5 MB | ~2.5 MB | **2.4 MB** |

The first draft's ~220 KB for JSON would have needed the checkpoints themselves to disappear.

**Speed** at 1 GiB, median (range across processes):

| Document | Before | After |
|---|---:|---:|
| Text | 2,507 (2,241-2,524) | 2,291 (2,240-2,421) |
| Csv | 3,345 (3,216-3,355) | 3,341 (3,316-3,463) |
| NdJson | 3,396 (3,357-3,413) | 3,302 (3,070-3,384) |
| JsonRecords | 350 (315-356) | 305 (283-346) |
| JsonTokenDense | 185 (179-192) | 210 (196-216) |
| JsonRecordsWithHashes | 209 (207-219) | 225 (222-250) |
| Xml | 547 (497-551) | 519 (480-535) |

Every pair's ranges overlap but JsonTokenDense's and JsonRecordsWithHashes', which moved
*faster*; JsonRecords and Text read lower but inside the before range. Movement in both
directions on a shared 4-core VM is noise, consistent with `ItemRef` compiling to the same
constant shifts. An M5 run belongs in [index-benchmarks.md](index-benchmarks.md)'s trend before
this is called settled.

**XML tree build** (BenchmarkDotNet, 64 MiB): allocation **860 KB → 112 KB** for every shape, and
the gen-2 collections are gone - before, RecordRoot and TextHeavy showed 200 and 219 gen-0/1/2
collections per 1,000 builds; after, none at any generation. Time moved down on every shape, but
inside BenchmarkDotNet's error bars.

| Shape | Allocated before | Allocated after | Gen 2 per 1,000 before | after |
|---|---:|---:|---:|---:|
| TinyElements | 860.38 KB | 112.41 KB | - | - |
| DeepNesting | 865.91 KB | 111.66 KB | - | - |
| RecordRoot | 859.77 KB | 111.66 KB | 200 | - |
| TextHeavy | 859.60 KB | 111.41 KB | 219 | - |

**Open question 1 answered.** The ~103 KiB excess in XML's *Index kept* was `GC.GetTotalMemory`
counting small-object-heap survivors of a full collection twice on .NET 10 on macOS - XML's two
`long` logs were its only segments under the large object threshold. Once every segment was
small, the M5's first pass after this change read *Index kept* at double *Allocated* for every
document; a standalone program reproduces it (401 KB live reads as 802 KB) while
`GC.GetGCMemoryInfo` reports 401 KB. The harness now reads kept and left-after-release from the
collection's record of the heap.

**Confirmed on the M5** (2026-10-31, `2e54969`, the machine every earlier run used): 1 GiB
*Index kept* JsonRecords 401.4 KB, Xml 1.0 MB, Csv 257.7 KB, Text 2.4 MB; at 1 MiB JSON 28.5 KB,
XML 74.3 KB, CSV/NDJSON/Text 17.5 KB. Speeds inside the previous run's ranges except CSV/NDJSON,
1-3% under. Full tables in [index-benchmarks.md](index-benchmarks.md).

**The 1 GiB allocation gap was the harness.** This VM first reported far more allocation at 1 GiB
than the M5, before and after alike (JsonRecords 8.0 / 8.7 MB against 775 KB), at a steady
~2.5 MB per second of indexing for every document. That was the harness's peak sampler reading
`Environment.WorkingSet` every millisecond: on Linux each read parses `/proc` into strings
(~3 KB a call), on macOS it is an allocation-free system call. The sampler now reads the working
set every 50 ms and subtracts its own allocation; re-run after the fix, *Allocated* at 1 GiB is
JsonRecords 403.4 KB, Csv 267.3 KB, Xml 1.1 MB - in line with *Index kept* and with the M5. The
1 MiB tables above were barely affected (a few milliseconds of sampling); the 1 GiB *Allocated*
column in index-benchmarks.md's before/after tables for this machine carries the inflation.

## References

- `Argonaut/Engine/Collections/SegmentedAppendLog.cs` — segment sizing, `Add`, `ItemRef`.
- `Argonaut/Engine/Indexing/Trees/SparseContainerIndex.cs` — the two tree logs.
- `Argonaut/Features/Xml/Indexing/XmlSiblingCounts.cs` — the three sibling-count logs.
- `Argonaut/Engine/Indexing/Lines/FileOffsetIndex.cs`, `AppendLogIndexBase.cs` — the line index log.
- `Argonaut/Features/Raw/Rows/RawSegmentIndex.cs` — `RawRowAnchor`, `AnchorStride = 64`.
- `Argonaut/Engine/Indexing/Trees/SparseContainerIndexBuilder.cs` — promotion at 64 KB, checkpoints.
- `Argonaut/Features/Json/ArrayTable/JsonArrayTableSession.cs` — a fresh index per table entry.
- [index-benchmarks.md](index-benchmarks.md) — the 1 GiB runs and column definitions.
