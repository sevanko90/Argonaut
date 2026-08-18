# Semantic JSON diff — implementation plan

> **Implementation status (2026-08-18, branch `json-diff`):** stages 0–5 implemented
> (`JsonRowFactory`/`LruCache`/`IndexGrowthMonitor`, content hashes, `JsonDiffIndex`,
> `JsonDiffSession`, `JsonDiffRowCollection`/`JsonDiffViewModel`/views, shell "Compare
> with…"). Deliberate deviations from this plan, each noted inline at the relevant spot
> in the code:
>
> 1. `StartIndexing` grew an **overload** instead of an optional `JsonIndexOptions`
>    parameter — optional parameters don't participate in method-group-to-delegate
>    conversion, which existing call sites rely on.
> 2. Move reconciliation **mutates the two records in place** (volatile,
>    `StatusBits`-gated) rather than "replacing them with one `Moved`" — the record log
>    is append-only, and stage 5 renders both positions anyway.
> 3. Arrays past `MaxAlignableArrayElements` are **not descended at all** (badged
>    approximate) instead of falling back to positional per-element records — positional
>    records for a 10M-element array (~240MB) would break the memory budget.
> 4. The number canonicalization decision: plain-integer spelling when it fits,
>    normalized scientific otherwise; see `JsonContentHasher` remarks.
> 5. The differ runs on a dedicated 32MB-stack thread — the descent recurses per nesting
>    level (up to the index's 4095 depth cap), too deep for a 1MB pool-thread stack.
> 6. Three-stage reveal, stage 1: the pre-diff live view is a **left-document preview**
>    (right pane fills when records stream) rather than both panes rendering live.
> 7. Stage 0.3 is partial: styles are shared (`JsonRowStyles.axaml`) and the diff panes
>    use a shared `JsonRowPresenter`, but `JsonView` keeps its inline row template — its
>    interaction wiring (search highlight, hint/jump buttons) is exactly the part the
>    plan says stays with the host, and swapping its template couldn't be visually
>    verified in this pass.
> 8. Expanded **Unchanged** subtrees mirror left-document content into both panes (the
>    right pane shows left key order); expanded Moved/Added/Removed subtrees walk their
>    own side. Closing-bracket rows and per-row date hints/schema gutter are not in the
>    diff view yet.
>
> Still open from "Deferred to follow-ups", additionally: changelist summary pane,
> leaf-level character diff, `EnsureVisible` linking between a move's two ends, paging
> ("show more") for capped diff levels.

Diff two JSON documents **by meaning** (path + content identity), not by position (byte offset,
line number, key order), without ever building an object graph and without regressing the
per-token memory budget the index already fought down to 24 bytes.

Core mechanism: a Merkle hash per token. Equal hashes ⇒ equal subtrees ⇒ the diff never descends.
Large unchanged regions cost O(1) to rule out, which is what makes this viable at Argonaut's scale.

## Constraints this plan is written against

1. **No DOM.** Nothing is parsed into a live object graph, on either side.
2. **Non-diff views pay nothing.** No hashes computed, no extra allocation, when diff isn't asked for.
3. **Diff runs in the background.** The UI stays fluent while both files index and while the diff
   itself runs. Progress reported through the existing `IProgressReporter`.
4. **Render as soon as there is enough data**, same as every other view — see "Three-stage reveal".
5. **Both files must be fully indexed before the diff starts.** Accepted; container hashes are only
   final when the container closes.
6. **Reuse the indexer.** The parse loop is a core system function. Extend it, do not fork it.
7. **Diff is an explicit mode** with its own view, its own toolbar, and its own row collection.

---

## Stage 0 — Extraction, no behaviour change

De-risks everything after it. Each step is independently shippable and covered by existing tests.

### 0.1 `JsonRowFactory`

Pull the "given (index, mmap, tokenIndex, schemaNodeId, arrayIndex) → `JsonRow`" logic out of
`JsonVisibleRowCollection` into its own class:

- `BuildRow` (the non-placeholder half), `BuildScalarText`, `BuildHint`, `BuildContainerSummary`,
  `DescribeChildCount`, `ReadText`, `FormatByteLength`, `FirstLine`/`EarliestOf`/`MaxSchemaLabelLength`.
- Holds `index`, `mmap`, `hintProviders`, `schema`, and `childCountCache`.
- `JsonRow` itself is unchanged and shared verbatim by both views.

`JsonVisibleRowCollection` keeps `Rebuild`/`AppendSubtree`/`visibleRows`/expand state and delegates
row construction. Pure move; `JsonVisibleRowCollectionTests`/`JsonRowTruncationTests`/`JsonSchemaRowTests`
should pass untouched.

**While here:** bound `childCountCache`. It currently survives every `Rebuild`, is never invalidated
and is never capped ([JsonVisibleRowCollection.cs:170](../Argonaut/Features/Json/JsonVisibleRowCollection.cs:170)).
Entries are immutable-once-known so eviction is always safe; give it the same LRU treatment as
`rowCache` with a generous cap. The diff view auto-expands changed subtrees, which will exercise this
far harder than manual browsing does.

### 0.2 `RowCacheLru<T>` and `IndexGrowthMonitor`

Two more verbatim extractions from `JsonVisibleRowCollection`:

- The `rowCache`/`rowCacheOrder` LRU pair (:512-535).
- The growth-poll timer + `AwaitIndexingCompletionAsync` (:964-1006), including the
  `GrowthPollInterval` rationale and the "await the indexing task so a fast file doesn't wait out a
  poll tick" behaviour. The diff collection needs exactly this against the diff task instead of an
  index.

### 0.3 XAML extraction

The row template is inline at `JsonView.axaml:195-281`, and the value-colouring styles
(`jsonString`/`jsonNumber`/`jsonBool`/`jsonNull`, `pathlink`, `hintlink`, `schemaContainer`) are
inline at `:18-64`. Both must be shared with the diff view.

- Move the styles into `Features/Json/JsonRowStyles.axaml` (a `Styles` resource), included by both views.
- Move the row body into a `JsonRowPresenter` `UserControl` taking a `JsonRow`, so the diff view can
  host two of them per row. Keep the expander/indent/gutter interaction wiring in the hosting view —
  only the *content* moves.

Verify visually: the JSON view must look identical after this step.

---

## Stage 1 — Content hashes as an opt-in extension of the existing index

### Decision: extend `JsonStructureIndex`, do not fork or post-process

Three options were considered:

| Option | Verdict |
|---|---|
| Subclass `JsonStructureIndex` | `Build` and the ctor are private, `PackedToken` layout is closed. Would need virtual hooks on the hot loop. No. |
| Second pass over the finished token index | Avoids touching `Build`, but re-reads every scalar's bytes and roughly doubles time-to-first-diff. Rejected. |
| **Optional second log, populated inside the same `Build` loop** | Reuses 100% of the parse. Chosen. |

### API

```csharp
public static JsonStructureIndex StartIndexing(
    MMapFile file,
    JsonIndexOptions options = default,   // new; default = ComputeContentHashes: false
    IProgressReporter? progressReporter = null,
    CancellationToken cancellationToken = default)
```

`JsonIndexOptions` is a readonly struct with one bool today. `IndexedFileSession.Start` takes a
`Func<MMapFile, IProgressReporter?, CancellationToken, TIndex>`, so the diff path passes a lambda that
closes over the options rather than the bare method group — no change to `IndexedFileSession`.

### Storage

A second `SegmentedAppendLog<long>` (`hashes`), **allocated only when `ComputeContentHashes` is set**,
indexed identically to the token log. 8 bytes/token when on, zero when off.

Cost when off: one `bool` field read per token inside the existing loop. Confirm against the
`RebuildCostBenchmarks` harness already in `Argonaut.Tests` before and after — the acceptance bar is
no measurable regression on the non-diff path.

### Computing the hash

`long`, not `ulong`, so `Volatile.Read`/`Volatile.Write(ref long)` apply directly.

- **Scalars** — hash the raw UTF-8 span straight off the mapping, no allocation:
  `XxHash3.HashToUInt64(file.GetSpan(offset, length))`, mixed with a per-kind salt so `"1"` (string)
  and `1` (number) never collide. Requires the `System.IO.Hashing` package (only new dependency).
- **Containers** — maintain a `Stack<HashAccumulator>` in lockstep with the existing `openContainers`
  stack. On close, finalize and write into the *Start* token's slot with `Volatile.Write`, then fold
  the result into the parent's accumulator. This is the same publish-then-mutate pattern already
  documented and in use for `EndIndex`
  ([JsonStructureIndex.cs:306](../Argonaut/Features/Json/JsonStructureIndex.cs:306)) — reuse that
  reasoning verbatim, including the `Volatile.Read` on the consuming side.
- **Objects combine commutatively**: `sum(mix(nameHash, valueHash))`. O(1) space per open container,
  and order-independence falls out for free. *Not* the spec's "hash the sorted child list" — that
  needs the whole child list buffered per level, which is unaffordable on a 10M-element container.
- **Arrays combine sequentially** — order is semantic there.

### The invariant everything downstream leans on

**A node's hash covers its content and nothing else — not its own key, not its path, not its
parent.** Note where the name goes in the object rule above: `mix(nameHash, valueHash)` is folded
into the *parent's* accumulator, so a child's own hash never contains its key.

Two things follow, and both are load-bearing:

1. A subtree's hash is **invariant under relocation**. Move it under a different parent, at a
   different depth, under a different key, and the hash is bit-identical. This is what makes
   cross-parent move detection possible at all (Stage 2).
2. Token *index* is never identity, only a handle into the log. Nothing in the diff ever compares
   "the node at position N on each side" — object children match by name, array children by
   anchored hash. Index shifts caused by an insertion or a relocation are structurally incapable of
   producing spurious differences.

A slot of 0 means "not final yet". Readers assert against it; the diff only runs post-`IsComplete`,
so it should be unreachable.

### Normalization: fast path / slow path

Full normalization (unescape strings, canonicalize numbers) means *decoding every scalar*, which
would forfeit the indexer's headline property. Split it:

- **Fast path (default):** hash the raw bytes.
- **Slow path**, taken only when the span contains an escape (`\`) for strings, or `.`/`e`/`E`/a
  leading `-0` for numbers. Normalize into a `stackalloc` buffer, hash that. No allocation either way.

On typical documents >95% of scalars take the fast path. Semantic equality is preserved; cost is near
zero.

**Open decision, needs settling before implementation:** the canonical number form. `1.0` vs `1` vs
`1e0`. Argonaut parses no numbers today. Recommend `decimal`-style canonicalization (strip trailing
fractional zeros, normalize exponent to plain notation where it fits) and document the chosen rule in
the type's remarks — this is a semantic promise users will notice.

### Collision risk

A false "unchanged" hides a real change — a visible correctness bug, not a perf nit. 64-bit XxHash3
with a strong finalizer puts birthday collision around ~5×10⁹ nodes, which is beyond the practical
document size. Ship 64-bit; note in the type's remarks that widening containers to 128-bit is the
escape hatch if it ever matters.

### Tests

- Property reordering ⇒ identical root hash.
- Array reordering ⇒ *different* root hash.
- `"café"` and `"café"` hash equal. `1.0`/`1`/`1e0` hash equal.
- `"1"` and `1` hash *unequal*.
- Nested container hash equals the hash of the same subtree parsed standalone.
- Options-off ⇒ `hashes` log is never allocated.

---

## Stage 2 — The differ (headless, fully testable without any UI)

`JsonDiffIndex : AppendLogIndexBase<PackedDiffRecord>` — same shape as the other scanners, so it gets
`WaitForCountAsync`, `IsComplete`, `Failure`, `RunIndexing`, and lock-free reads for free.

```
DiffStatus: Unchanged | Added | Removed | Modified | Moved
record struct DiffRecord(int LeftToken, int RightToken, DiffStatus Status, int Depth, int ParentRecord)
```

`-1` on either token side means absent. Records are emitted in **merged render order**, so the record
log *is* the flattened diff tree — the row collection walks it directly rather than re-deriving order.

### Algorithm

Start at both roots. If hashes match, emit one `Unchanged` container record and stop — the entire
document is done. Otherwise descend:

- **Objects** — build a transient `name-hash → child token index` map for *this one level* of each
  side. Match by name hash, verify by byte comparison of the actual name spans (guards against name
  collisions cheaply, since it only runs on matched pairs). Matched + equal hash ⇒ `Unchanged`, stop.
  Matched + different hash ⇒ recurse if both containers, else `Modified`. Unmatched ⇒ `Added`/`Removed`.
  The transient maps are dropped on ascend — memory is O(widest changed level), not O(document).

  **No global path map.** A `Map<path, IndexRecord>` would materialize a JSON Pointer string per
  token: ~80-140 bytes for the string plus ~24 for the dictionary entry, i.e. 150-250 bytes/token
  against the current 24. `ParentIndex` chains already encode the path (`JsonPathBuilder` proves it
  is O(depth) with nothing materialized).

- **Arrays** — see Stage 3.

Note what this descent does *not* do: it matches children only *within* a level, so a subtree that
moved to a **different parent** appears as `Removed` at its old path plus `Added` at its new one.
Correct, but it reads as a rewrite. The reconciliation pass below fixes that.

### Cross-parent move reconciliation

Runs after the descent, over the emitted records. Cheap precisely because `Added`/`Removed` subtrees
are emitted whole and never descended into — this pass is bounded by the size of the *change*, not
the document, and the Merkle short-circuit already guarantees a small change yields few records.

1. Bucket `Removed` container records by hash; bucket `Added` container records by hash.
2. A hash present in both buckets **exactly once on each side** pairs up: replace the two records
   with one `Moved` carrying both token indexes. Paths are built lazily by `JsonPathBuilder`
   (O(depth), nothing materialized) only when the row is actually rendered.
3. A hash appearing 2+ times on either side is ambiguous — leave those as `Added`/`Removed`. Same
   unique-anchor rule Stage 3 applies to arrays.

Deliberate limits:

- **Containers only.** A scalar `1`/`true`/`""` hashes identically everywhere by design; including
  scalars would emit constant spurious "moves".
- **Property rename with unchanged content** (`{"a": X}` → `{"b": X}`) is the same phenomenon and
  falls out of this pass for free — no separate rename detection needed.
- **Moved *and* modified will not match**, because the hash genuinely changed. See below — this one
  is a real hole, not an acceptable limitation.

### Similarity pairing (v2, designed here so the v1 shape doesn't preclude it)

Exact-hash pairing only catches subtrees that moved *unchanged*. Move a block and edit one value
inside it and both sides fall through to `Removed` + `Added`.

**The cost is not cosmetic.** Because neither side was descended into, the interior diff is lost
too: relocating a 200-key config block while changing one port renders as 200 removed rows and 200
added rows, with nothing marking the 199 that are identical. That is worse than a line differ would
manage, which is not a defensible place for a semantic diff to land.

The fix runs on the residue left *after* exact-hash pairing, which for a normal diff is a handful of
records — the Merkle short-circuit guarantees it.

1. For each surviving unmatched pair (removed `R`, added `A`), score by **direct-child hash
   overlap**: collect each side's `(nameHash, valueHash)` set for direct children only — O(children),
   each child reachable in O(1) via `EndIndex` skipping — and take the Jaccard index.
2. Pair greedily by descending score, mutually-best-match, above a threshold (start ~0.5).
3. A paired container is emitted as `Moved` **and then run back through the ordinary descent**, so
   the result is `Moved` on the container plus `Modified` on the single leaf that actually changed.
   No new rendering machinery — it composes with everything above.

Guards, all of which matter:

- Same container kind only (object↔object, array↔array).
- Skip the whole pass when `|R| × |A|` exceeds a pair cap (~1000). A degenerate diff falls back to
  plain `Added`/`Removed` rather than going quadratic.
- **Direct children only, no recursive similarity.** Recursive scoring is where the cost would
  actually live, and it buys little over one level.
- **One round only.** The reconciliation-triggered descent emits its own unmatched `Added`/`Removed`
  records; those do *not* get a second similarity pass. This is what makes termination trivial
  rather than a fixpoint argument with a depth cap. Exact-hash pairs need no follow-up descent at
  all, since their content is unchanged by definition.

**Sequenced after v1 deliberately.** Not for cost — the pass is cheap. The threshold and the greedy
pairing want tuning against real diffs, and there is no way to tune them before the exact-hash pass
exists to show how much residue actually falls through. Ship exact-hash, measure the residue, set
the threshold from evidence. What v1 must not do is foreclose it: keep the reconciliation pass a
distinct stage over records (not fused into the descent), and keep the descent re-entrant on an
arbitrary (leftToken, rightToken) pair rather than assuming it only ever starts at the roots.

Tests (v1): relocate a subtree across parents ⇒ exactly one `Moved`, zero `Added`/`Removed`;
relocate one of two identical subtrees ⇒ stays `Added`/`Removed` (ambiguous); rename a key over
unchanged container content ⇒ one `Moved`; relocate *and* edit ⇒ `Added`/`Removed` (asserting the
known v1 hole, so the v2 pass has a test to flip).

Tests (v2): relocate *and* edit ⇒ one `Moved` plus `Modified` on the changed leaf only; two
similar-but-distinct candidates ⇒ the higher-scoring pair wins and the other stays `Added`/`Removed`;
residue beyond the pair cap ⇒ pass skipped, no quadratic blowup, results identical to v1.

### Merged key order (for rendering)

Base sequence is the left document's key order at that level; walk the right document's keys and
insert any key absent from the left at its relative position from the right. Same LCS idea as the
array alignment, applied to key names. Gives both panes one shared order so a formatter's reordering
doesn't visually scramble the tree.

### Threading

Runs on `Task.Run` via `RunIndexing`, publishing records incrementally. Cancellation checked on the
same `& 0xFFFF` mask cadence the token loop uses, so teardown resolves in single-digit milliseconds.
Progress reported as records emitted against an estimate (left token count is a fine denominator).

---

## Stage 3 — Array alignment

Plain Myers/LCS is O(ND), quadratic when everything differs, and needs the whole child-hash sequence
buffered (a 10M-element array = 80MB transient). One pathological array would hang the diff. Use git's
histogram approach instead:

1. **Anchor pass, O(N):** histogram the child hashes on both sides; hashes appearing exactly once on
   each side and matching become anchors.
2. **Myers only inside the gaps between anchors.** In practice gaps are small.
3. **Hard cap:** an array over `MaxAlignableArrayElements` (start at 100,000) with no usable anchor
   structure falls back to positional diff, and the container row is badged *"alignment approximate"*.

The cap is part of the design, not a bug to discover later. Elements that align but differ recurse as
`Modified`; anchored-but-relocated elements emit `Moved` with their source index, so a list reorder
does not render as a wholesale rewrite.

### Tests

- Insert one element at the head of a 1,000-element array ⇒ 1 `Added`, 0 `Modified`.
- Reorder two elements ⇒ 2 `Moved`, 0 `Added`/`Removed`.
- Array with all-identical elements (no anchors) still terminates within the cap.
- Array beyond the cap falls back and flags itself.

---

## Stage 4 — Lifecycle: `JsonDiffSession`, cancellation, disposal

The riskiest part of the whole feature. Today the shell owns exactly one `IndexedFileSession`, and
`docs/architecture.md` is emphatic about the release ordering: **cancel → join indexing → join
dependents → release mapping**. The mapping is released via cached pointers; disposing under a
running scan is a native use-after-free, not a catchable exception.

The diff task reads spans from **both** mappings, so it cannot be a `RegisterDependentTask` of either
session — neither one may release its mapping while it runs.

```csharp
public sealed class JsonDiffSession : IDisposable
{
    IndexedFileSession<JsonStructureIndex> Left;
    IndexedFileSession<JsonStructureIndex> Right;
    JsonDiffIndex Diff;
    CancellationTokenSource diffCts;   // linked to Left.Token and Right.Token
}
```

Disposal order, and it is not negotiable:

1. `diffCts.Cancel()`
2. join `Diff.IndexingTask` (swallow cancellation/fault, as `IndexedFileSession.Dispose` already does)
3. `Left.Dispose()` — which itself cancels, joins its scan, joins its dependents, releases its mapping
4. `Right.Dispose()`
5. `diffCts.Dispose()`

Idempotent, same as `IndexedFileSession` — the diff view model and the view's detach handler will both
call it.

### Shell integration

`JsonDiffViewModel : IDocumentViewModel` owns the `JsonDiffSession` internally. This keeps
`MainWindowViewModel`'s single-`CurrentDocument` invariant completely intact — no change to
`SetCurrentDocument`, the disposal chain, or the staleness guard.

Consequences to handle:
- The diff is entered explicitly (a "Compare with…" command), **not** via `FileTypeDetector`, so it
  gets no `FileKind` and no entry in `DocumentViewCatalog.DisplayOrder`. The view switcher must not
  offer it; switching *away* from it disposes it via the normal outgoing-document path.
- `CreateSearchNavigator()` returns **null** in v1 (the find bar hides itself, exactly as
  `IncompatibleViewModel` already does). Two-file find is a follow-up, not a v1 blocker.
- `IndexFailure` on either side must name *which* side failed. Extend the diff view model's status,
  not `IndexFailure` itself.
- Recent files: record both paths, but don't offer the diff as a reopen target in v1.

### Tests — required, per the brief

Precedent for all of these already exists in `IndexedFileSessionTests`, `CollectionDisposedEmptyTests`
and `StatusProgressHandoffTests`.

1. Dispose while the diff task is mid-run ⇒ returns promptly, diff task observed cancelled, **both**
   mappings released only after the join.
2. Dispose while one side is still indexing and the other is complete.
3. Dispose while *both* sides are still indexing (diff never started).
4. `JsonDiffRowCollection` reports `Count == 0` and `this[i] == null` once disposed — mirroring
   `CollectionDisposedEmptyTests`, so the trailing `ItemsSource` walk during a content swap reads
   nothing.
5. A span read racing disposal surfaces as a catchable `ObjectDisposedException` from `MMapFile`,
   never an access violation.
6. One side fails to index ⇒ the diff never starts, the failure is attributed to the correct side,
   and the other side's mapping is still released cleanly.
7. Double dispose (view model + view detach handler) is a no-op the second time.
8. Cancellation mid-`Build` with hashes enabled leaves no partially-final container hash observable
   (the sentinel-0 assertion holds).

---

## Stage 5 — The diff view

### Row model: one list, not two synced panes

Two independent collections with scroll sync would be an alignment hack that breaks under
virtualization. Instead: **one** `JsonDiffRowCollection : MemoryMappedCollectionBase`, each row
carrying both sides.

```csharp
sealed class JsonDiffRow
{
    JsonRow? Left;          // null when absent on the left
    JsonRow? Right;
    DiffStatus Status;
    int Depth;
    bool IsExpanded;
    string? MoveBadge;      // "moved from [2] to [0]", or "moved from /config/db"
}
```

Both `JsonRow`s come from `JsonRowFactory` (Stage 0.1) — one factory per side, each bound to its own
index/mmap/schema. Rendered as a single `ListBox` with a two-column item template hosting two
`JsonRowPresenter`s. Alignment becomes structural and scroll sync is free.

The walk is a straight iteration over `JsonDiffIndex`'s records (already in merged order) honouring
expand state, with the same child caps and "show more" placeholders as the existing collection. It
does **not** share `AppendSubtree` — that walk is driven by one index's parent chain, this one by
alignment, and unifying them would double the state space of the app's subtlest class for no gain.

`IndexGrowthMonitor` (Stage 0.2) polls the diff index instead of a token index. Identical live-append
behaviour, identical `isPureAppend` optimization.

Default expand state comes free from the walk: `Unchanged` containers collapse (rendering
`foo: {5 keys, unchanged}`), changed subtrees auto-expand down to the differing leaf.

A **cross-parent `Moved`** row has two positions in the merged tree — its old one and its new one —
and rendering it in only one leaves a hole in the other pane. Render it at *both*: at the source, a
collapsed stub reading `db: moved to /meta/db →`; at the destination, the real row badged
`↕ moved from /config/db`, collapsed by default since its content is unchanged. Both stubs carry
both token indexes, so clicking either reveals the other via `EnsureVisible`.

### Three-stage reveal

Keeps the "no loading spinner" promise even though the diff itself needs both indexes complete:

1. **Immediately** — both panes render live from their own token indexes as they stream in, no diff
   colouring, status reads "indexing both documents".
2. **Both indexes complete** — the diff task starts; status switches to diff progress.
3. **Diff records stream in** — rows gain status colouring/badges progressively via the growth
   monitor, top-down, so the top of the document is usable before the bottom is aligned.

### Toolbar and per-pane concepts

`JsonDiffToolbarViewModel`, separate from `JsonToolbarViewModel`, exposed through the existing
`IDocumentViewModel.Toolbar` seam — add one type-keyed `DataTemplate` in `MainWindow.axaml` alongside
the JSON and Raw ones and the shell needs no other change. Date-hint settings and expand depth reach
the toolbar the same way `JsonToolbarViewModel` already takes them (a `DateHintSettings` instance plus
an `applyExpandDepth` callback in its ctor), so there is no shell-side concrete-type matching to add.

What needs a "which pane" concept:

- **JSONPath breadcrumb** — a row has up to two token indexes and therefore up to two paths. Bind to
  the *focused* pane; on an `Added`/`Removed` row only one side exists, so use that one.
- **Schema gutter** — a schema aligns with one document. v1: bind a schema to one nominated side and
  render a single gutter against it. Two independent gutters is a follow-up.
- **Date hints / expand depth** — apply to both panes symmetrically. `JsonDiffViewModel` owns one
  `DateHintSettings` and one expand depth and fans them out to both sides, the same way
  `NdJsonViewModel` already fans its master `HintSettings` down into its nested per-line
  `JsonViewModel`. No shell involvement.
- **New: filter mode** — "changes only" vs "full tree". Cheap; it is a predicate in the walk.

### Changelist summary

A second, flat `ListBox` above the tree, bound to a filtered projection of the same diff records
(`Modified`/`Added`/`Removed` only). Values re-sliced from the mappings on demand via `JsonRowFactory`
— never stored duplicated in any index. Clicking an entry calls the tree's `EnsureVisible`, reusing
the existing ancestor-expansion machinery.

### Leaf-level character diff

Only on `Modified` scalar rows, only between the two already-decoded display strings (both already
capped at `DisplayText.MaxLength`). Nothing structural, no scale concern.

---

## Sequencing

Stages 0–2 have no UI surface and are independently mergeable behind no flag at all — 0 is a pure
refactor, 1 is dormant unless `ComputeContentHashes` is set, 2 is a headless library with unit tests.
Stage 3 is self-contained algorithm work. Stage 4 is the one that touches shell lifecycle and should
land with its full test set before any of Stage 5 exists. Stage 5 is the only stage that can't be
validated without the others.

Suggested order: 0.1 → 0.2 → 1 → 2 → 3 → 0.3 → 4 → 5.

## Deferred to follow-ups

- **Similarity pairing for moved-and-edited containers** — designed in Stage 2, sequenced after v1.
  The highest-value item on this list: without it, a relocated-and-edited block loses its interior
  diff entirely.
- Find across both panes.
- Two independent schema gutters.
- Diff as a reopenable recent-files entry.
- 128-bit container hashes.
- Persisting a diff as a patch (RFC 6902) — the diff records are already close to this shape.
