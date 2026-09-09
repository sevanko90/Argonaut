# Editing a file in place — options considered

Decision record for "let the user change the document, not just look at it". Nothing here is
built. This document exists to settle *where* editing would live and *how* an edit is represented
before any of it is scheduled, because the naive answer (mutate bytes, re-index) is the one
answer that a multi-GB tool cannot afford.

Every cost claim below was checked against the code at the time of writing (branch `main`,
2026-09-09), with file/line references kept so a future reader can tell whether the reasoning
still holds or the code has moved out from under it.

The constraint that drives every choice: the app never holds a document in memory. It holds a
mapping (`MMapFile`) and an index, and realizes only the rows on screen. Any editing design that
requires the document in memory, or requires re-deriving a whole index per keystroke, is
disqualified on arrival regardless of how clean it looks.

## Decisions at a glance

- **How an edit is represented** — a piece table over (original mapping, append-only scratch),
  with logical offsets (option D). Rejected: mutating the file (A), copy-on-open (B). Kept as a
  cheaper special case for the JSON view only: an offset-keyed replacement overlay (C).
- **Which view gets the editor** — the raw view first, and possibly only (option B). Rejected:
  editing in the JSON tree first (A). Deferred: both (C).
- **What an edit costs the index** — raw re-derives, JSON overlays. The raw index is a pure
  function of the bytes and can be invalidated and regrown; the JSON index is a record of parse
  results and cannot.
- **How a save works** — streaming rewrite to a temp file, then atomic rename. Rejected:
  in-place patching.
- **What happens to search while dirty** — search reflects the last save, and the document is
  marked dirty. Deferred: a merge-iterator over piece-space.

## 1. How an edit is represented

### A — Mutate the file

Write the change into the file on disk and re-index.

Any edit that changes a length has to shift every subsequent byte of the file, which is a full
rewrite per keystroke. Even a same-length overwrite invalidates every content hash the diff
indexer computed (`JsonStructureIndex` lines 338, 410-411) and forces a re-index. Disqualified
on the first sentence — this is the option that only works because it is how a small-file editor
already behaves, and it is exactly what does not scale here.

### B — Copy the document into memory on first edit

Read the file into a mutable buffer, edit freely, write it back.

Honest and simple, and it is what most editors do. It also forfeits the entire premise of the
app: a 3GB file becomes a 3GB (or, decoded, larger) allocation, on top of what the JSON
structural index already costs at that size — 24 bytes per token, about 2.24 GiB at 100M tokens
([index-memory-analysis.md](index-memory-analysis.md)). Disqualified.

### C — Offset-keyed replacement overlay

A dictionary from a token's original byte offset to a span in a scratch buffer. Reads consult it
before falling back to the mapping.

The attraction is that it fits the existing code almost invisibly. Every byte read in the app
funnels through `MMapFile.GetSpan(long offset, int length)` (`Argonaut/Infrastructure/MMapFile.cs:64`)
or its decoding sibling `GetUtf8String` (line 86) — 36 call sites app-wide, 9 of which are
`FileTypeDetector` probing the file at open rather than a display path. Crucially, in the JSON
paths those calls pass a *token's* offset and length, taken from a `JsonTokenInfo`
(`Argonaut/Features/Json/JsonStructureIndex.cs:44-52`) that is frozen in original-file
coordinates. So a replacement keyed by original offset can be served at the chokepoint with the
callers unchanged, and the index never learns that anything moved.

What it cannot express: anything that changes the *number* of bytes in a way the walk must see,
which is to say insertion and deletion. It is a genuine option only for "replace this value with
that value", and only where the index is structural rather than positional.

**Kept, but narrowly** — see §3, where it is the whole of a JSON-view stage 1.

### D — Piece table over (original mapping, append-only scratch) (chosen)

The document becomes an ordered list of pieces, each `(buffer, offset, length)` where buffer is
either the original mapping or an append-only scratch buffer. An edit splits at most two pieces
and inserts one. Nothing is ever mutated or moved; the scratch buffer only grows.

- Memory is proportional to *edits*, not to file size. Human-scale editing means a scratch
  buffer measured in KB against a file measured in GB.
- A logical offset maps to a physical `(buffer, offset)` by binary search over the piece list.
  That is the one new cost every read pays, and it is a handful of comparisons against a list
  whose length is the number of edits.
- Insertion and deletion are the same primitive as replacement, so undo/redo is a command log
  over one operation rather than four.
- It is a well-understood structure with no novel parts. The risk is in what consumes it, not
  in the structure.

**Chosen.** It is the only option that keeps the memory story intact while supporting real
editing, and option C degrades out of it cleanly for the JSON case.

## 2. Which view gets the editor

### A — The JSON tree

Edit values and keys in place in the tree, keeping structure valid by construction.

This is the feature a user would ask for, and it is the expensive one, because the JSON index
records parse results. Rebuilding it is a full re-parse of the file. A profiling run on a 1GB
synthetic document (2026-07-24, harness not committed) measured JSON indexing at ~283-428 MB/s
against ~1024 MB/s for the raw scan, which is a SIMD newline walk with no token parsing. So the
index cannot
be re-derived per edit, and every edit that changes structure has to be expressed as an overlay
that the row walk merges in. §3 breaks down what that costs.

It also inherits every reader that is positional rather than structural: array element counts,
JSONPath indices, the container child-count cache
(`Argonaut/Features/Json/JsonRowFactory.cs`, `ChildCountCacheCapacity`), schema row binding.

**Not first.** The most valuable version of this is also the most expensive, and it is not
reachable without the byte layer that option B needs anyway.

### B — The raw view (chosen to go first)

The raw view is currently treated as a fallback: a flat, virtualized list of display rows that
makes no assumptions about content, substituting U+FFFD for invalid UTF-8 and Unicode Control
Pictures for C0 controls (`Argonaut/Features/Raw/RawRowReader.cs`). That stance is exactly
right for an editor of last resort, and the view is far cheaper to make editable than the tree
for one structural reason:

**`RawSegmentIndex` does not store rows. It derives them.** It stores one `RawRowAnchor`
(16 bytes) per `AnchorStride = 64` rows (`Argonaut/Features/Raw/RawSegmentIndex.cs:48`), and
`GetRowInfo` (line 87) recovers any row in between by rescanning forward from the bucket anchor
through `NextRowBoundary` (line 285) — a bounded rescan of at most `AnchorStride × (WrapWidth + 1)`
bytes. The index is ~2.3MB for a 1GB file and is a pure, deterministic function of the bytes.

That changes the whole problem. Where the JSON view must *overlay* because it cannot rebuild,
the raw view can simply *re-derive*. Point `NextRowBoundary` and `RawRowReader.ReadRow` at
piece-space instead of file-space and the view is editable, with no structural overlay at all:
no tombstones, no fragment indices, no merge-walk. There is no structure to merge.

**Chosen to go first.** It is also the capability a big-file tool most conspicuously lacks: a
4GB JSON document that fails to parse at byte 3.2 billion cannot be repaired by any
structure-aware editor, because there is no structure to bind an edit to. The raw view is the
one view that still works when the document is broken.

### C — Both, sharing the byte layer

The end state, if editing earns it. Worth naming now only to record the sequencing: the piece
table built for B is the thing A needs, so A becomes a smaller project after B ships than before.

There is also a route where A is never built. If a save from the raw view triggers the re-index
the app already performs on open, the JSON view gets edited content for free, at the cost of the
user editing in raw and switching back. That trade should be re-evaluated after B, not now.

**Deferred.**

## 3. What an edit costs each index

### The JSON index — overlays, in three ascending classes

1. **Replace a scalar or a member name.** Served entirely by option C at the `GetSpan`
   chokepoint. The index is untouched, because it still correctly describes the original file
   and nothing downstream ever learns the replacement's length except the display path that
   reads it. Small, and self-contained.
2. **Delete a member or element.** Not a byte edit but a walk edit: tombstone the token range
   `[token, token.EndIndex]`. The visible-row walk already skips subtrees by `EndIndex`, so the
   skip itself is nearly free. The cost is in everything that *counts*: array indices for
   JSONPath, container child counts (cached by token index and deliberately surviving rebuilds —
   would need invalidating), schema row binding.
3. **Insert or paste a subtree.** The only class that needs a second structure. New content has
   no tokens, so it is parsed into its own small `JsonStructureIndex` over the scratch buffer —
   cheap, because inserted text is human-scale — and anchored to `(parentToken, positionAmongChildren)`.
   The row walk then becomes a merge-walk over original children and spliced fragments. This is
   the expensive class, and it is confined to `JsonVisibleRowCollection`'s walk.

### The raw index — deltas and bounded re-flow

An edit at logical offset X:

- Anchors *before* X are untouched.
- Anchors *after* X are valid with a constant `(byteDelta, lineDelta)` applied. No rescan.
- `lineDelta` is zero unless the edit inserted or deleted a `\n`.
- Re-flow caused by a forced wrap break is **bounded by the line**, not by the file, because a
  forced break is measured from the line start and a line ends at `\n`.

So a keystroke costs a bounded local rescan plus a delta entry, not a rescan of the tail. The
pathological case is worth stating plainly rather than hiding: editing inside a single 100MB
line re-flows that line.

Publishing the re-derived rows needs no new machinery. `RawSegmentIndex` is already an append log
that grows during a background scan and publishes at anchor boundaries (`publishedRowCount`,
`WaitForRowCountAsync`), and the view already handles growth and a final refresh. Invalidation is
"truncate the log at the affected anchor and let it regrow" — the path the index already takes on
first open.

## 4. How a save works

### Rejected — patch the file in place

Only expressible for same-length edits, and even then it destroys the file if the process dies
mid-write. Not worth having as a fast path.

### Chosen — streaming rewrite, then atomic rename

Walk the pieces in order and copy each to a temp file beside the original, then fsync and
rename. For the JSON overlay case the equivalent walk is over the original file, copying
verbatim until an edited offset, emitting the replacement, skipping tombstoned ranges to
`EndIndex`, splicing fragments at their anchors.

Either way it is one sequential pass at mapping speed — on the order of seconds for a 1GB file,
taking the raw scan's measured ~1024 MB/s as the ceiling for a pure byte copy — followed by the
background re-index the app already performs on open. Save cost is O(file), which is what every
editor pays and is not where the difficulty of this feature lives.

## 5. Search and the other direct readers

The readers that bypass the index and read the file themselves are where the sprawl is, and they
are the reason this is a coordinate-system change rather than a feature:

- **`FileSearchSession` opens its own mapping of the path, one chunk at a time**
  (`Argonaut/Features/Search/FileSearchSession.cs:160`). It scans the bytes on disk and cannot
  see a piece table or an overlay.
- **`RawOffsetRowResolver`** maps a byte offset back to a row for reveal — needs piece-space.
  Small.
- **`JsonDiffIndex`** reads name spans from its own two mappings, and the content hashes it
  relies on are computed during indexing from raw spans, so edits invalidate them.
- **`JsonArrayRowCollection` / `JsonArrayElementIndex`** match column names off `NameOffset`,
  which survives an option-C replacement but not an insertion or deletion.
- **`CsvFieldReader`, `NdJsonLineReader`** read spans directly and would need the same treatment
  if editing ever reaches those views.

**Chosen for now:** search reflects the last save, and the document carries a dirty flag that
says so. It is honest, it is cheap, and it does not compromise the fastest path in the app.
**Deferred:** a merge-iterator over piece-space, which is a project in its own right.

## 6. The caret

Worth recording because it inverts the intuition about where the work is. The piece table is a
few hundred lines of well-understood code. Turning a virtualized, read-only row list
(`Argonaut/Features/Raw/RawView.axaml.cs`) into something with a caret, selection spanning
rows, IME support, clipboard, and keyboard navigation that behaves correctly when rows are
being derived on demand underneath it, is very likely the larger half of the feature.

Any estimate for this work that prices the data structure and not the caret is wrong.

## Outcome

Build the byte layer once, in the raw view.

1. **Piece table + raw view reads.** `NextRowBoundary` and `RawRowReader.ReadRow` over
   piece-space, anchor deltas and bounded re-flow, `RawOffsetRowResolver` in piece-space. No UI
   yet — testable headlessly against a scripted edit sequence, in the shape the existing index
   tests already use.
2. **Editing UI in the raw view.** Caret, selection, clipboard, undo/redo command log. The large
   half; size it separately and do not fold it into step 1.
3. **Save.** Streaming rewrite, temp file, atomic rename, background re-index. Dirty flag, and
   search marked as reflecting the last save.
4. **Re-evaluate the JSON view.** With 1-3 shipped, decide between the option-C overlay for
   scalar edits in the tree (small, self-contained, no index change) and leaving structural
   editing to the raw view. Do not commit to JSON edit classes 2 and 3 before this point.

Stopping after step 3 leaves a genuinely useful tool. That is the main argument for this
sequencing over the one that starts in the tree.

## Related

- [json-array-table-options.md](json-array-table-options.md) — the "export a subtree to file"
  idea noted there is adjacent to this work and shares the streaming-write path.
- [index-memory-analysis.md](index-memory-analysis.md) — the 24 bytes/token index cost quoted
  above, and the field split behind `PackedToken`. The throughput figures are from an
  uncommitted 2026-07-24 profiling harness and are not recorded in the repo.
- [roadmap.md](roadmap.md) — where this sits against everything else deferred.
