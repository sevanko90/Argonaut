# The index's fixed cost: what every open pays, and the plan to remove it

**Status:** finding and proposal. Nothing in "Plan" is implemented yet.

**Measured:** 2026-10-31, production code at commit `76312d2` (the working tree adds only
benchmarks and this document), Apple M5 (10 cores), macOS 27.0.1, .NET 10.0.5.
**Harness:** `Argonaut.Tests/Benchmarks/IndexMemoryBaseline.cs`, run with `--index-memory` at
1, 16 and 256 MiB; the 1 GiB figures are the recorded run of 2026-10-28 in
[index-benchmarks.md](index-benchmarks.md#runs).

This document is written to be checked. Every number has the command that produced it, the source
that explains it, and a statement of what would falsify it. The open questions at the end are
things this analysis does not explain.

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

Totals when every log has been touched: **XML ≈ 832 KiB, JSON ≈ 576 KiB, CSV/NdJSON/raw ≈ 128 KiB.**

Three of the XML segments and both of JSON's are over the 85,000-byte large-object threshold, so
they are LOH allocations that only a gen-2 collection reclaims.

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

Above those thresholds the fixed cost is the same at 64 KB as at 1 GiB.

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

### C. At 1 GiB it is still the majority of the index

Recorded run of 2026-10-28 (1 GiB, same harness):

| Document | Allocated | First segments (fixed) | Fixed share |
|---|---:|---:|---:|
| JsonRecords | 774.8 KB | 576.0 KiB | **74%** |
| JsonTokenDense | 774.4 KB | 576.0 KiB | 74% |
| JsonRecordsWithHashes | 779.2 KB | 576.0 KiB | 74% |
| Xml | 1.5 MB | 832.1 KiB | **54%** |
| Csv | 267.0 KB | 128.0 KiB | 48% |
| NdJson | 267.0 KB | 128.0 KiB | 48% |
| Text | 2.5 MB | 128.0 KiB | 5% |

At 1 GiB JSON's item count is about 16,370 and its allocation is three segments (one containers,
two checkpoints) — still essentially all fixed. The published per-GiB figures therefore describe
the fixed cost, not a per-byte rate: JSON's marginal cost is about 0.19 MB/GiB and XML's about
0.69 MB/GiB.

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
| 1 GiB | 0.012% | 0.055% | 0.079% |

So a 1 MB XML file allocates almost as much for its index as the file is large, a 10 KB CSV
allocates thirteen times its size, and a 1 GiB file allocates a rounding error's worth. The
absolute cost is constant; the *rate* is not.

### E. It is LOH, and it collects in gen 2

The 85,000-byte threshold is why the heap column moves in steps. `8192 × 24 = 196,608` and
`8192 × 48 = 393,216` are both over it; `8192 × 8 = 65,536` is not. The XML build benchmark at
64 MiB showed gen-0 = gen-1 = gen-2 = 166.67 collections per 1,000 operations for three of four
shapes — about one full collection per six builds — while the process allocated only ~860 KB per
build. The harness's three cycles per process at 1/16/256 MiB reported 0/0/0, so the frequency
depends on how many sessions run before the LOH budget trips; it is not "every open", but it is
routine churn that a 16 KiB allocation would not produce at all.

## Why it exists

`SegmentedAppendLog<T>` is the index's lock-free record store: one background writer appends, the UI
thread reads. Segments are a **fixed 8,192 items** so that an item's address is
`segments[i >> 13][i & 8191]` — two constant shifts, a table load and no bounds arithmetic beyond
the published count. The writer rewrites the table by copy-and-swap, so a reader never sees a
relocated array, and the volatile count publish is the entire lock. The design is good and should
stay; the **first segment's size** is the problem, and it is a constant that nothing sizes to the
document.

## Does JSON do the same? Yes — and so do CSV, NDJSON and Raw

- **JSON tree** (`JsonSparseIndex` → `SparseContainerIndex`): the same two segments, 576 KiB, for
  any document ≥ 64 KB. This is per JSON *session*: opening a file, each side of a diff, and every
  rebuild after a save.
- **The JSON array table** indexes its array on every entry (`JsonArrayTableSession.Start` passes
  no kept structure), so entering a table over an array ≥ 64 KB allocates ~576 KiB each time.
  The roadmap already has an item to keep that index.
- **CSV, NDJSON and Raw** each pay one 128 KiB `FileLineAnchor` / `RawRowAnchor` segment, and they
  pay it for **any non-empty file** because anchor 0 is added unconditionally — a 10 KB CSV
  allocates thirteen times its size.
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

### Step 1 — size segments by bytes (the change)

In `SegmentedAppendLog<T>`, replace the fixed `SegmentShift = 13` with a per-closed-generic value
derived from `Unsafe.SizeOf<T>()`, targeting ~16 KiB:

| Record | Bytes | Items/segment | Segment |
|---|---:|---:|---:|
| `ContainerSlot` | 48 | 256 | 12,288 B |
| `TreeCheckpoint` | 24 | 512 | 12,288 B |
| `Entry` / anchors | 16 | 1,024 | 16,384 B |
| `long` | 8 | 2,048 | 16,384 B |

Expected first-segment totals: **JSON ≈ 24 KiB, XML ≈ 72 KiB, CSV/NdJSON/raw ≈ 16 KiB**, all in
gen 0, none on the LOH. With the same items, the 1 GiB figures should become roughly:

| Document | Before | After (expected) |
|---|---:|---:|
| JsonRecords | 774.8 KB | ~220 KB |
| Xml | 1.5 MB | ~780 KB |
| Csv / NdJson | 267.0 KB | ~155 KB |
| Text | 2.5 MB | ~2.4 MB (variable-dominated) |

### Step 2 — verify both ends

1. Re-run the harness at 1, 16, 256 MiB and 1 GiB. Acceptance:
   - 1 MiB total allocation under ~45 KiB for JSON, ~120 KiB for XML, ~24 KiB for CSV/raw;
   - allocation still flat with size (it must now be *small*, not just constant);
   - `Index kept` drops by the fixed difference and gen-2 collections stay at zero;
   - 1 GiB speed within the recorded ranges (the change touches the read hot path).
2. Re-run the XML tree build benchmark (`--filter "*XmlTreeIndexBuild*"`) and confirm 860 KB →
   about 100 KB and no gen-2.
3. Run the full test suite, JSON and XML views unchanged.

**Hot-path risk.** `ItemRef` would read a `static readonly` shift instead of a constant. If the
1 GiB speeds regress, fall back to a single `const SegmentShift = 10` (1,024 items): every segment
is then at most 48 KiB (under the LOH threshold), `ItemRef` is byte-identical, and the fixed cost
is ~104 KiB XML / ~72 KiB JSON instead of ~72/~24. Measure before choosing; the per-type version is
only worth its extra code if it is free at 1 GiB.

### Step 3 — guard it

- A deterministic test over the log types asserting no segment reaches 85,000 bytes, via an
  internal `SegmentBytes` on `SegmentedAppendLog<T>` and on `SparseContainerIndex`.
- A test that indexes a 1 MiB JSON and a 1 MiB XML document and asserts process allocation under a
  generous bound (~200 KiB) — it fails today at 583/862 KiB. Tests already run one at a time
  (`CollectionBehavior(DisableTestParallelization = true)`), so a process-wide allocation delta is
  stable enough.
- Record the 1 MiB point in [index-benchmarks.md](index-benchmarks.md) so the curve stays visible,
  rather than only 1 GiB runs that hide the fixed cost.

### Step 4 — follow-ups, not part of this change

- **Array table kept index** (already on the roadmap): removes a repeated 576 KiB per entry for
  arrays ≥ 64 KB.
- **`GC.AllocateUninitializedArray`** for segments: skips zeroing 850 KB per XML open, but does
  nothing for the generation; only worth it after Step 1.
- Revisit the three sibling-count logs only if the 72 KiB remaining is worth chasing; merging the
  two `long` logs is possible, the `entries` log is not.

## Risks and what would falsify this

- **Falsified if** a 1 MiB JSON document did not allocate ~583 KiB, or if allocation scaled with
  size between 1 and 256 MiB. It did neither: the table in Evidence A is the test.
- **Falsified if** a sub-64 KB **tree** document allocated the tree segments. The promotion gate
  says it cannot; a reviewer can add a 32 KiB case to confirm (the harness's minimum is 1 MiB).
  CSV, NDJSON and Raw are different by construction: their anchor at offset 0 is unconditional, so
  they allocate one segment for a tiny file too — that is read from the code, not measured here,
  and a 10 KB case is worth measuring directly.
- **Falsified if** the records were larger than assumed. `ContainerSlot` 48 B and `TreeCheckpoint`
  24 B are the arithmetic on the table; a reviewer should confirm with `Unsafe.SizeOf<T>()` before
  trusting the byte totals.
- **Risk:** `ItemRef`'s shift is no longer a compile-time constant. This is the one thing that
  could cost real throughput; the 1 GiB speeds are the check.
- **Risk:** more segments means more segment allocations and a larger table during a scan. At the
  item counts in this app (sparse containers, one checkpoint per 64 KB, anchors per 64 KB/1,024
  lines) that is tens to hundreds of segments, not thousands.
- **Not addressed:** the per-open cost is paid once per session; how often sessions really happen
  (file open, array-table entry, save/rebuild, diff sides, long NDJSON lines) is a product question
  this document does not measure.

## Open questions for the reviewer

1. **The XML `Index kept` column reads ~103 KiB above both `Allocated` and `Peak heap`, at every
   size** (964.1/964.5 KiB kept vs 861.5 KiB allocated and 889.1 KiB peak). It is constant, it is
   XML-only, and the computed 832.1 KiB plus 29.4 KiB of scan scratch does not explain it. Likely
   LOH accounting in `GC.GetTotalMemory`, but it is not proven. It does not affect the finding —
   `Allocated` and `Peak heap` agree with the arithmetic — but a reviewer should check it with
   `GC.GetGCMemoryInfo()` or a heap dump before quoting "kept" for XML.
2. **Gen-2 frequency.** The harness does three cycles per process and sees none; the build
   benchmark does eight builds and sees one every ~six. A reviewer with a small harness change
   could run 20 sequential opens and count collections, which would let Step 3's test be
   frequency-based rather than allocation-based.
3. **Is 16 KiB the right target** rather than 8 or 32 KiB? 16 KiB is chosen to stay well under
   85,000 bytes while needing only tens of segments at 1 GiB. The trade-off is segment count against
   first-segment size, and it should be settled by the 1 GiB run, not by argument.

## Reproduce

```sh
dotnet build -c Release Argonaut.Tests/Argonaut.Tests.csproj

# The three points in Evidence A (each takes seconds to half a minute)
dotnet run -c Release --no-build --project Argonaut.Tests -- \
  --index-memory --size-mib 1   --out BenchmarkDotNet.Artifacts/index-memory-1mib.md
dotnet run -c Release --no-build --project Argonaut.Tests -- \
  --index-memory --size-mib 16  --out BenchmarkDotNet.Artifacts/index-memory-16mib.md
dotnet run -c Release --no-build --project Argonaut.Tests -- \
  --index-memory --size-mib 256 --out BenchmarkDotNet.Artifacts/index-memory-256mib.md

# The 64 MiB XML build benchmark (BenchmarkDotNet)
dotnet run -c Release --no-build --project Argonaut.Tests -- \
  --filter "*XmlTreeIndexBuild*"
```

The harness writes deterministic documents to a temp directory and runs each case in three fresh
processes. `Allocated` is a process-wide `GC.GetTotalAllocatedBytes(precise: true)` delta around
open → index, `Peak heap` is sampled, `Index kept` is live heap after a forced collection with the
session still open, `Left after release` is the same after disposal. KB/MB in its report are
1024-based.

## References

- `Argonaut/Engine/Collections/SegmentedAppendLog.cs` — segment constant, `Add`, `ItemRef`.
- `Argonaut/Engine/Indexing/Trees/SparseContainerIndex.cs` — the two tree logs.
- `Argonaut/Features/Xml/Indexing/XmlSiblingCounts.cs` — the three sibling-count logs.
- `Argonaut/Engine/Indexing/Lines/FileOffsetIndex.cs`, `AppendLogIndexBase.cs` — the line index log.
- `Argonaut/Features/Raw/Rows/RawSegmentIndex.cs` — `RawRowAnchor`, `AnchorStride = 64`.
- `Argonaut/Engine/Indexing/Trees/SparseContainerIndexBuilder.cs` — promotion at 64 KB, checkpoints.
- `Argonaut/Features/Json/ArrayTable/JsonArrayTableSession.cs` — a fresh index per table entry.
- `Argonaut/Features/NdJson/NdJsonViewModel.cs` (line ~437) and `Argonaut/Features/Json/JsonViewModel.cs`
  (line ~276) — a fresh index per selected line.
- [index-benchmarks.md](index-benchmarks.md) — the 1 GiB run and column definitions.
