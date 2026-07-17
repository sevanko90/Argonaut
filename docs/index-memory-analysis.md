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

### 1. `uint` instead of `long` for `Offset`/`NameOffset`

- `uint.MaxValue` = 4,294,967,295 → caps indexable file size at **~4 GiB**.
- Saves 8 bytes/token (2 fields × 4 bytes) for zero added complexity.
- Real hard cap — needs confirming against actual expected max file sizes
  before committing.

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

### 3. `NameOffset` as a small delta, not an absolute offset

A property name sits immediately before its value (`"name": value`), typically
just a handful of bytes away. Storing `NameOffset` as a small back-offset from
`Offset` (a `ushort` or `byte` with overflow fallback) instead of a full 4-byte
absolute offset saves another ~2-3 bytes/token in the common case.

**Options 1+2+3 combined get to ~24 bytes/token (~150-170MB for the 50MB
case) with minimal complexity/risk. Good first pass.**

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
