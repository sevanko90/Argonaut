# Performance & memory review — 2026-07-17

Findings from a full review of the indexing and viewing paths, ranked by expected impact
for the target files (4GB NDJSON / 1.8M lines, and 60MB JSON / 6M tokens). Status reflects
work on the `memory-optimise` branch.

## Performance

### 1. NDJSON newline scan was byte-at-a-time — IMPLEMENTED
`FileOffsetIndex.ProduceOffsets` walked all 4GB one byte per loop iteration. Replaced with
`ReadOnlySpan<byte>.IndexOf((byte)'\n')`, which is SIMD-vectorized (typically 10–30× faster
for a pure scan), running directly over the mapped bytes (see finding 2).

### 2. Every byte read from the mmap was copied before use — IMPLEMENTED
`MMapFile` only exposed `MemoryMappedViewAccessor.ReadArray` into rented arrays, so both
indexers copied the whole file through small buffers and every realized row copied again
just to decode a string. `MMapFile` now acquires the view pointer once and exposes
`ReadOnlySpan<byte> GetSpan(long offset, int length)`:

- The NDJSON scan runs `IndexOf` directly over mapped memory — zero copies.
- `Utf8JsonReader` parses the mapped bytes directly. Sub-2GiB files (spans are capped at
  `int.MaxValue`) are parsed in a single pass, which also removed the 64KB chunking loop,
  reader-state resumption, and the grow-and-retry path for oversized tokens.
- Line/token text reads decode straight from the span — no rent/copy per row.

Bounds still come from `FileInfo.Length`, never accessor capacity (see CLAUDE.md);
`GetSpan` throws on any request past the real end of file.

### 3. `DescribeChildCount` is O(children) per realized row and recomputed constantly — IMPLEMENTED
`JsonVisibleRowCollection.DescribeChildCount` walked up to 50,000 tokens per collapsed
container row, and the row cache is cleared by every `Rebuild` (every 250ms during
indexing), so visible containers recounted over and over. A container's child count is
immutable once its `EndIndex` is known (and the count only runs then), so counts are now
cached in a `Dictionary<int, int>` that deliberately survives `Rebuild` — each container
is counted at most once for the lifetime of the view.

### 4. `Rebuild` fires every 250ms even when nothing visible changed — IMPLEMENTED
Once every visible subtree is fully indexed (usually within the first second), further
token growth cannot change `visibleRows`, but `OnGrowthTick` still rebuilt and raised
`Reset` every tick because `TokenCount` keeps moving — forcing the viewport to re-realize
and re-decode all visible text for the remainder of indexing. `Rebuild` now tracks a
`visibleTreeSettled` flag during the `AppendSubtree` walk — cleared when any visible
container's `EndIndex` is still unknown (every "not indexed yet" truncation happens under
such a container) — and growth ticks skip the rebuild while the flag holds. Expanding
into an unindexed region clears the flag via its own `Rebuild`, so ticks resume
rebuilding when the view can actually change.

### 5. Per-token lock traffic in `JsonStructureIndex.Build` — IMPLEMENTED
Each of the ~6M tokens took the lock for `Add`, often again for the `EndIndex` fix-up,
and `NotifyCountReady` locked a third time even with no waiter (~15M lock round-trips per
index). Rather than batching appends under fewer locks, both indexers now publish through
`SegmentedAppendLog<T>` — a lock-free single-writer/multi-reader segmented list whose only
synchronization point is a volatile count (release on append, acquire on read). Fixed
segments never move, so the `List<T>`-resize hazard that forced locking doesn't exist;
`EndIndex` (the one field mutated after publication) uses paired `Volatile.Read`/`Write`.
This also removed the capacity-estimate/`TrimExcess`/LOH-churn machinery and made
`TokenCount`/`GetToken`/`GetLineSpan` lock-free for the UI thread. Measured ~23% faster
JSON indexing (warm steady state: ~120ms → ~92ms for 46MB / 4.9M tokens); NDJSON indexing
unchanged (scan-dominated) but its UI read path no longer locks either.

## Memory

### 6. NDJSON line index can halve: store offsets, not spans — DEFERRED
`FileLineSpan` is 16 bytes (long + int, padded); 1.8M lines ≈ 29MB. Lines are contiguous,
so length is derivable from the next line's offset (last line ends at file length). A
`List<long>` of line starts is 8 bytes/line. Do not use `uint` — 4GB files sit exactly at
the wraparound.

### 7. `ParentIndex` costs 4 bytes/token and nothing in the app reads it — DEFERRED
Only tests consume `JsonTokenInfo.ParentIndex`; no UI feature does. Dropping it takes
`PackedToken` from 24 to 20 bytes (~24MB at 6M tokens). Keep it if a "jump to parent"
navigation feature is planned.

### 8. `TrimEnd('\r','\n')` allocated every line twice — IMPLEMENTED
Both `ReadLine` implementations decoded the full line to a string, then `TrimEnd`
allocated a second nearly identical string. Now the trailing newline bytes are trimmed
from the span before decoding, and the two duplicate `ReadLine` implementations
(`NdJsonViewModel` / `MemoryMappedFileLineCollection`) were consolidated into
`NdJsonLineReader`.

### 9. Every producer computed its own percent and gating — IMPLEMENTED
`IProgressReporter.Report` took raw `(bytesProcessed, totalBytes)`, so `FileOffsetIndex`
computed a percent just to decide whether to call (`percent % 5 == 0`), and
`StatusProgressReporter` computed percent again to decide whether to post, deduping on
every 1%-point change. `Report` now takes `(string message, long? current, long? max)` -
producers just hand over the raw offsets (or nothing, when there's no total to report
against) - and `StatusProgressReporter` is the only place that turns that into a percent,
appends it to the message, and dedupes, now bucketed to every 5 percentage points instead
of every 1.

## Minor notes (not planned)

- `FileTypeDetector` also scanned with byte loops, but stops at the second line; it was
  moved to vectorized span scans as part of finding 2 anyway.
- Out-of-range rows in `MemoryMappedFileLineCollection.GetLine` re-allocate placeholder
  objects on every query during growth; negligible.
