# JSON tree: a sparse index and a drawn surface

The JSON tree's memory grows with token count, not file size. `JsonStructureIndex` holds a 24-byte
`PackedToken` for every token, End tokens included, plus 8 bytes a token when content hashes are on.
JSON tokens are 2-15 bytes, so the index is 2-3x the file for typical nested documents and ~12x for
token-dense ones (`[1,2,3,…]`); a 1GB file profiles at ~2.8GB of heap. No packing fixes this: any
per-token record is linear in tokens, and at 2 bytes a token even a 2-byte record matches the file.
The index has to be sparse - anchors to jump into the file, with everything between them re-parsed
on demand.

The view is the other half. The tree is a `ListBox` over `JsonVisibleRowCollection`, an `IList`,
and an `IList` needs an exact `Count` and random access to row *i* of the flattened expanded tree.
That demand is what makes a dense, globally numbered row space look necessary. A drawn surface
like `RawTextSurface` needs only an anchor, a cursor and an extent estimate, which is exactly what a
sparse index provides. The two changes are done together; either one alone keeps most of the cost.

## Goals

- Index memory a small, bounded fraction of the file (target: under 1%), for any JSON shape.
- First paint without waiting for any indexing.
- No per-container child caps, no "show more", no full rebuild on expand/collapse.
- Live view during indexing with no `CollectionChanged` churn.
- Every existing consumer (paths, search, schema, hints, array table, diff, NDJSON line pane) keeps
  working.

## The index

**Large containers only.** A container is recorded only if its byte span is at least `T`
(default 64KB): start, end, child count, parent, depth - about 32 bytes. Smaller containers and all
scalars are never stored; parsing up to `T` bytes when a row is realised costs microseconds.
Large containers with no large child are disjoint, so there are at most `F/T` of them; only
single-child chains (`[[[[…]]]]`) add more, and a depth cap on what is recorded bounds those.

**Child checkpoints inside large containers.** At the first child boundary after every `B` bytes
(default 64KB), record `(offset, childOrdinal)`. A checkpoint is always a value start inside a
known container, so the parser resumes with its ancestor stack taken from the large-container
chain - never mid-token, never by reconstructing `JsonReaderState`. `arr[5_000_000]` is a binary
search on ordinals and a skip of at most `B` bytes of siblings; a large sibling is skipped by its
recorded end.

**Size.** For 1GB at the defaults: ~16K container records and ~16K checkpoints, about 1MB.
`T` and `B` may later be derived from a memory budget rather than fixed.

**Optional per-depth row counts.** Each checkpoint can carry cumulative token counts per relative
depth 0..15 (64 bytes a checkpoint). With them the row count for any uniform expand depth is exact
and the scrollbar row-accurate; overrides adjust by the toggled container's own counts. The surface
does not need these - an estimated extent works - so they are an increment, not a prerequisite.

**Append-only and growth-safe.** Both logs are append-only, so the rules in CLAUDE.md about
`AvailableLength`, `LengthSettled` and `WaitForLength` apply unchanged. A container's end is
written once when it closes, the same publish-then-mutate pattern `EndIndex` uses today.

**Identity is a byte offset.** A row is `(offset, kind)`: a container's start, a scalar's start, or
`(end offset, Close)` for a closing bracket. Expand overrides, selection and bookmarks are keyed by
offset, which is stable under growth and shared with the raw view and search without translating
through token numbers. There is no `TokenIndex`, `ParentIndex` or `EndIndex`: parents come from the
cursor's stack and ends from a container record or a skip scan within `T`.

## The structural scanner

Skipping and re-parsing only stay cheap if they run at GB/s, faster than `Utf8JsonReader`. Use the
simdjson stage-1 approach over `Vector256`/`Vector512`: bitmasks of quotes, backslashes and
structural characters, with string interiors masked out by a carry-less-multiply prefix XOR
(PCLMULQDQ on x64, PMULL on ARM). Skipping a subtree is then depth counting over the structural
mask. `Utf8JsonReader` stays for decoding the rows that are shown.

The stage-1 scan does not fully validate. The index build validates in the same pass and keeps only
the first error offset, so the failure reporting `DescribeFailure` does today still has an offset
to report.

## The surface

A drawn control in the pattern of `RawTextSurface`: `ILogicalScrollable`, fixed row height, the
visible range computed from the scroll offset, and one `Render` pass for text, expand toggles,
selection, find highlights, schema gutter and hint badges.

- **Anchor**: `(offset of first visible row, pixel sub-offset)`.
- **Cursor**: a `JsonRowCursor` with `MoveNext`/`MovePrevious` over display rows under the current
  expand state, carrying the ancestor stack - the JSON counterpart of `RawRowCursor`.
- **Forward** is a plain parse from the anchor, so rendering needs no index at all; the index is a
  seek accelerator.
- **Backward** re-parses from the previous checkpoint (at most `B` bytes). Decoded rows are cached
  per checkpoint span in a small LRU of blocks, so memory follows the viewport, not the file.
- **Seek** (thumb drag, search hit, path navigation, go to parent) descends the large-container
  chain to the nearest checkpoint and parses forward.
- **Extent** is an estimate - byte-proportional, or exact where per-depth counts exist. When the
  estimate changes, the anchor stays put and the correction goes into the offset, so content never
  jumps (the CodeMirror 6 approach to estimated heights).
- **Expand state** stays "default depth XOR override", with the overrides in an ordered set of
  container offsets.
- **Sticky ancestor headers** fall out of the cursor knowing its ancestor chain, and are worth
  having at multi-GB depths.

Shared machinery moves down rather than sideways: scroll arithmetic, row height, horizontal pan,
gutters and caret/selection plumbing go to `Ui/RowSurface`, used by both `RawTextSurface` and the
JSON surface, each supplying only a cursor and a row painter.

What the `ListBox` gave for free has to be built: keyboard navigation, focus, hit testing, context
menus, tooltips and automation peers. Accessibility is the one most likely to be forgotten, so it is
part of the surface work, not a follow-up. Row templates become draw code; cache a `TextLayout` per
visible row.

## Consumers

| Consumer | Today | With the sparse index |
|---|---|---|
| `JsonVisibleRowCollection` | materialised `visibleRows`, `Rebuild` per toggle | replaced by the surface and `JsonRowCursor` |
| `JsonRowFactory`, hints, schema resolution | per token index | per cursor row; schema node inherited down the cursor's stack |
| `JsonPathBuilder` | parent chain of token indices | cursor stack, or container chain plus a parse of at most `T` |
| `JsonPathResolver` | token walk | checkpoint ordinals for array steps; key scan skipping values via the structural mask for object steps |
| `JsonOffsetTokenResolver`, `JsonSearchNavigator` | offset to token by binary search | offset to container chain, then parse at most `T` to the row |
| `JsonArrayElementIndex`, `JsonArrayRowCollection` | token walk | the container's checkpoints are the element index |
| `JsonArrayColumnDiscovery`, `JsonDocumentKeySampler` | token walk | sample a spread of checkpoint spans |
| `DateHintInference` | token walk | sample, as above |
| `JsonDiffIndex`, `JsonDiffRowCollection`, `JsonContentHasher` | 8-byte Merkle hash per token | see Diff below |
| NDJSON's hosted `JsonViewModel` | small per-line index | unchanged in shape; a line is usually below `T`, so no index at all |

## Diff

Diff is the one consumer that wants per-node data: aligning the children of a large array of small
elements wants a hash per child. It gets its own budget rather than blocking the rest:

- Hashes recorded only for large containers and for each checkpoint span; small subtrees hashed on
  demand while aligning, which reads at most `T` bytes each.
- If alignment inside a large array needs per-child hashes, they go in a separate log spilled to a
  memory-mapped temp file for the diff session's lifetime, not into the shared index.

The existing dense index stays available to diff until this is built, so diff is migrated last.

## Order of work

Each step lands on its own and leaves the app working.

1. **Benchmarks first.** A BenchmarkDotNet suite over three shapes - a token-dense array, deeply
   nested objects, a large array of small records - measuring index bytes per file byte, build time,
   time to first row, seek latency and backward-page latency. Run against the current index to fix
   the baseline.
2. **Structural scanner** in `Engine`, with no Avalonia reference: stage-1 masks, subtree skip, and
   a validating mode. Tested against `Utf8JsonReader` on a corpus including escapes, surrogates,
   strings containing brackets and quotes across chunk boundaries, and `GrowingByteSource`.
3. **Sparse index**: large containers and checkpoints, built by the scanner as an
   `IBackgroundIndex`, alongside the existing index rather than replacing it.
4. **`JsonRowCursor`**: forward and backward over display rows with expand state, cross-checked
   against a walk of the dense index on the same corpus - the dense index is the test oracle.
5. **`Ui/RowSurface`**: extract the shared parts of `RawTextSurface`, with the raw view unchanged in
   behaviour.
6. **JSON surface** replacing the tree `ListBox`, including keyboard navigation, selection, schema
   gutter, hints and accessibility.
7. **Consumers** moved one at a time per the table above: paths and search, then array table and
   samplers.
8. **Diff** on its own hash budget.
9. **Remove `JsonStructureIndex`'s dense log** once nothing reads it, and the `ChildCap`/"show more"
   machinery with it.
10. **Per-depth row counts**, if the estimated scrollbar proves not good enough in use.

## Open questions

- Default `T` and `B`, and whether they scale with file size or a memory budget. Settle from the
  step-1 benchmarks.
- Whether closing brackets remain rows. Today they are; the cursor can emit them either way.
- Key lookup in objects with millions of keys: a linear key scan is GB/s, but a path resolver that
  repeats it may want a sparse key-hash filter per large object.
- Whether `Vector512` is worth a separate path over `Vector256` on the hardware this runs on.
