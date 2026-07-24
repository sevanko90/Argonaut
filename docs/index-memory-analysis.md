# JSON structure index memory analysis

## Problem

Indexing a 50MB JSON file leaves ~400MB of managed RAM resident after indexing
completes — several times the file size. Loading itself is streaming (see
`Features/Json/JsonStructureIndex.cs`, `Build`): the file is read from the
`MMapFile` in 64KB chunks via `ArrayPool<byte>` rented buffers, run through a
streaming `Utf8JsonReader`, and no token text is ever decoded/retained during
indexing (`ReadText()` in `JsonVisibleRowCollection.cs` decodes on demand and
returns the buffer immediately). The UI is also lazy: `JsonVisibleRowCollection`
only materializes `JsonRow`s for expanded/visible content (capped at
2000/20,000 children per container, 1000-row LRU cache).

**Conclusion: the memory is the structural index itself**, not duplicated file
bytes or UI elements.

## Where it comes from

`JsonStructureIndex` keeps one `JsonTokenInfo` struct per JSON token (every
scalar and every Start/EndObject/Array — `PropertyName` is folded into the
following value's `NameOffset`/`NameLength`) in a `List<JsonTokenInfo>`.

Current layout (`JsonStructureIndex.cs:29-37`):

```csharp
public record struct JsonTokenInfo(
    JsonTokenKind Kind,   // int, 4 bytes
    int Depth,            // 4 bytes
    long Offset,          // 8 bytes
    int Length,           // 4 bytes
    int ParentIndex,      // 4 bytes
    int EndIndex,         // 4 bytes
    long NameOffset,      // 8 bytes
    int NameLength);      // 4 bytes
```

Sum = 40 bytes/token (up to 48 with CLR padding).

For a 50MB file with a typical flat-record shape, token counts can run into
the several millions (~8 tokens per record × hundreds of thousands of
records). E.g. ~8M tokens × 48 bytes ≈ 384MB — matches the observed ~400MB.

Same pattern exists in the NDJSON path (`Features/NdJson/FileOffsetIndex.cs`),
just cheaper per unit: `FileLineSpan(long Offset, int Length)` ≈ 16 bytes/line.

## Options considered (from cheapest/safest to most aggressive)

**Status: options 1-3 were implemented, but not exactly as first planned below
— see the "As actually implemented" note after each. Net result is still 24
bytes/token, reached via a better field split than originally proposed. Current
code: `Argonaut/Features/Json/JsonStructureIndex.cs:119-127` (`PackedToken`).**

### 1. `uint` instead of `long` for `Offset`/`NameOffset`

- `uint.MaxValue` = 4,294,967,295 → caps indexable file size at **~4 GiB**.
- Saves 8 bytes/token (2 fields × 4 bytes) for zero added complexity.
- Real hard cap — needs confirming against actual expected max file sizes
  before committing.

**As actually implemented:** superseded by option 2 below — `Offset` was
packed at 48 bits alongside `Kind`/`Depth` instead of shrunk to a separate
`uint`. Net effect is better than planned: the indexable file-size cap is
**~256 TiB**, not ~4 GiB, for the same total byte count.

### 2. Bit-pack `Kind`/`Depth`/`Length`/`NameLength` into one `ulong`

Instead of splitting a `long` into two 32-bit halves (no win — that's just two
`int`s), pack unequal-width fields into a single 64-bit word:

| Field        | Bits | Max value          |
|--------------|------|---------------------|
| `Kind`       | 4    | 16 (9 needed)        |
| `Depth`      | 12   | 4,095                |
| `Length`     | 24   | ~16.7M               |
| `NameLength` | 24   | ~16.7M               |

4+12+24+24 = 64 bits exactly, one `ulong` replacing four `int`s (16 bytes → 8
bytes for that group).

Combined with option 1: `Offset`(4) + `NameOffset`(4) + `ParentIndex`(4) +
`EndIndex`(4) + packed word(8) = **24 bytes/token**, down from 40 (~40%
reduction). For the 50MB case: ~400MB → **~240MB**. Low risk, no perf cost.

**As actually implemented:** the word packs `Kind`(4) + `Depth`(12) +
**`Offset`(48)** instead of `Kind`/`Depth`/`Length`/`NameLength` — `Length`
and `NameLength` were left unpacked. `PackedToken` (24 bytes total):

```csharp
public struct PackedToken
{
    public ulong Packed;      // [Offset:48][Depth:12][Kind:4], MSB..LSB
    public int Length;        // unpacked, uncapped beyond int.MaxValue
    public int ParentIndex;   // unpacked
    public int EndIndex;      // unpacked; mutated post-publish, Volatile.Read/Write
    public ushort NameDelta;  // see option 3 below
    public ushort NameLength; // unpacked, ~64K cap (never hit in practice)
}
```

Same 24-byte total as planned, reached by giving `Offset` the packed-word room
instead of `Length`/`NameLength`: this removes the 4 GiB file cap entirely at
zero cost, since `Length` never needed more than `int.MaxValue` anyway and
`NameLength`'s ~64K cap (vs. the plan's 16.7M) is never approached by real
property names.

### 3. `NameOffset` as a small delta, not an absolute offset

A property name sits immediately before its value (`"name": value`), typically
just a handful of bytes away. Storing `NameOffset` as a small back-offset from
`Offset` (a `ushort` or `byte` with overflow fallback) instead of a full 4-byte
absolute offset saves another ~2-3 bytes/token in the common case.

**As actually implemented:** exactly this — `NameDelta` is a `ushort`
back-offset from `Offset`; if the true gap doesn't fit (pathological
whitespace between a name and its value), `NameDelta` stores the sentinel
`0xFFFF` and the real `NameOffset` is stashed in a side
`Dictionary<int, long>` keyed by token index, guarded by a dedicated lock.
Expected empty in practice.

**Options 1+2+3 combined reached ~24 bytes/token (~150-170MB for the 50MB
case) with minimal complexity/risk, as planned — via a better field split
than originally proposed here.** The storage container also changed,
independently of byte layout: tokens live in `SegmentedAppendLog<PackedToken>`
(lock-free, single-writer/multi-reader segmented array), not the
`List<JsonTokenInfo>` described in "Where it comes from" above, which
describes the pre-optimization state only — see
`docs/perf-review-2026-07-17.md` finding #5.

### 4. Delta + varint encoding (bigger win, real complexity)

Offsets are strictly increasing. Switch from array-of-structs to
structure-of-arrays, and delta+varint-encode the offset column — most
consecutive-token deltas are small enough to fit in 1-2 varint bytes instead
of 4. Could realistically reach **8-12 bytes/token average** (~100-150MB for
the 50MB case).

Trade-off: breaks O(1) random access to `GetToken(i)`. Needs periodic
checkpoints (e.g. absolute offsets every 1024 tokens) with delta replay from
the nearest checkpoint on lookup — bounded and cheap (sub-microsecond), but a
real added layer vs. the current flat indexer.

**Estimated numbers (2026-07-24 re-evaluation, not yet prototyped/measured):**

Random access (offset binary search, ancestor-chain walk via `ParentIndex`,
jumping into a collapsed subtree via `EndIndex`) would cost O(checkpoint
interval) instead of today's O(1): worst case, replay every varint delta from
the nearest checkpoint back up to the target. Sequential/skip-ahead walks
(the dominant access pattern — see tree rendering in
`JsonVisibleRowCollection.AppendSubtree`) stay near O(1) amortized, since a
stateful decode cursor just keeps advancing rather than re-seeking.

Assuming ~3ns per token to replay a delta (pure ALU: shift/mask + add, no
parsing — well under the ~18.8ns/token the *original* `Utf8JsonReader` build
pass costs per perf-review finding #5):

| Checkpoint interval | Worst-case replay per random `GetToken` | ~50 random lookups (one tree-open interaction, generous) |
|---|---|---|
| 1024 | ~3.1 µs | ~150 µs |
| 512  | ~1.5 µs | ~75 µs |
| 128  | ~0.4 µs | ~20 µs |

All three are 2-3 orders of magnitude below the ~16ms/frame budget and far
below the ~100ms human-perceptible-lag threshold — **no felt lag at any of
these intervals**, even under generous assumptions about how many random
lookups one interaction needs. The real cost of option 4 is implementation/
debugging complexity (replaying JSON nesting state correctly from a
checkpoint), not runtime latency.

Estimated total index size at 100M tokens (current 24 bytes/token baseline:
100M × 24B ≈ **2.24 GiB**), using the 8-12 bytes/token average above (10B/token
midpoint) plus checkpoint overhead (~16 bytes/checkpoint: absolute offset +
depth/kind + parent, so random access doesn't need to replay past it):

| Checkpoint interval | Checkpoints | Checkpoint overhead | Base (100M × ~10B) | Estimated total |
|---|---|---|---|---|
| 1024 | ~97,656  | ~1.5 MiB  | ~954 MiB | **~955 MiB** |
| 512  | ~195,313 | ~3.0 MiB  | ~954 MiB | **~957 MiB** |
| 128  | ~781,250 | ~11.9 MiB | ~954 MiB | **~966 MiB** |

**Checkpoint interval barely moves total size** — it's dominated by the
per-token varint-delta average, not checkpoint count, since even 781K
checkpoints only add ~12MB against a ~954MB base. Its real effect is bounding
worst-case random-access replay length. Since a smaller interval is close to
free on size, **128 looks like the better default over 1024** if option 4 is
ever built — tighter latency bound for negligible extra bytes. All figures
above are estimates from field-width reasoning, not measurement; the real
8-12 bytes/token average depends on the actual distribution of consecutive
offset deltas in real files and should be measured with a prototype before
committing.

### 5. Block compression (probably not worth it)

Compress fixed-size chunks of the packed struct array (e.g. every 4096
tokens) and decompress-on-demand with an LRU cache of decoded blocks — same
pattern already used for `JsonRow` in `JsonVisibleRowCollection`. JSON
structure is repetitive so ratios could be good, but this adds a
decode-and-cache layer on top of what's currently a flat array indexer. Payoff
over option 4 is marginal for the added complexity — not recommended unless
option 4 still isn't sufficient.

## Recommendation

Start with options 1-3 (safe, cheap, ~40% reduction, no architectural change).
Revisit option 4 only if that's insufficient. Skip option 5 unless option 4
still falls short.

**Status (2026-07-24): options 1-3 implemented — current index is 24
bytes/token via `PackedToken`, see options 1-3 above for the as-shipped field
split (better than originally planned: no 4 GiB file cap). Option 4 has not
been implemented; re-evaluated above with concrete size/latency estimates —
looks worth pursuing whenever the ~2.24 GiB/100M-token baseline needs to
shrink further, since the added random-access cost is imperceptible (low
microseconds) and the complexity is confined to decode logic, not threading.**

A separate idea (memory-mapping the index file itself, keeping it out of
managed RAM entirely rather than shrinking its per-token footprint) was
considered and set aside: this index is read synchronously on the UI thread
(tree rendering, `DescribeChildCount`, offset resolution), and the whole
point of `SegmentedAppendLog`'s lock-free design (perf-review finding #5) was
to guarantee that path never stalls. Memory-mapping would reintroduce a stall
risk (a page fault on a cold page blocks the UI thread) that today's
all-managed-array design specifically doesn't have, in exchange for RAM
savings that option 4 already captures without that risk. Not recommended
unless option 4 (and dropping unused `ParentIndex`, perf-review finding #7)
both prove insufficient.
