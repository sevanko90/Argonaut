# JSON array table — options considered

Decision record for the "view a JSON array as a table" feature. The implementation that came out
of this is [json-array-table-plan.md](json-array-table-plan.md); this document holds the options
that were weighed and discarded, so the *why* survives the plan getting trimmed to the work.

Every cost claim below was checked against the code at the time of writing (branch
`optimize-json-diff-alignment`, 2026-08-27), with file/line references kept so a future reader can
tell whether the reasoning still holds or the code has moved out from under it.

How the table renders *nested* objects and arrays is decided separately, in
[json-array-nesting-options.md](json-array-nesting-options.md).

## Decisions at a glance

- **Where the table lives** — swap `CurrentDocument` for a table document with a banner and a Back
  link (option C). Rejected: inline in the tree (A), split pane (B), separate OS process (D).
- **Who owns the mapping** — the table opens its own sub-range session (C1). Deferred: sharing the
  origin document's session to skip the reindex (C2).
- **How the shell reveals a location** — capability interfaces the document opts into. Rejected:
  a second concrete-type match, and reveal delegates on `DocumentViewCatalog`.
- **How column widths get measured** — free measurement (spans / token lengths) over the whole
  initial-paint batch. Rejected: caching decoded sample text, and a two-row sample.

## 1. Where the table lives


### A — Inline, embedded in the tree

Render the table directly where the array node sits in `JsonVisibleRowCollection`, in place of
its expanded children rows.

- Breaks the single flat-row virtualization model that gives Argonaut its speed
  (`JsonVisibleRowCollection` → one virtualized `ListBox`). Embedding a second, differently-shaped
  `ItemsControl` mid-list means either a non-virtualized grid (fine only under a row cap — wrong
  instinct for a "big files" tool) or teaching the row collection to interleave two row kinds,
  which is deep, risky surgery on the piece of the app under the tightest perf budget.
- No banner/back-link needed — there's nothing to navigate back from.
- **Cost: highest.** Touches the core virtualization path for a payoff (a few dozen rows inline)
  that's smaller than the other two options. Not recommended.

### B — Split pane, JSON and table visible together

Add a second, independent `IDocumentViewModel` slot to the shell, rendered side-by-side with
`CurrentDocument`.

- `MainWindowViewModel` has exactly one `CurrentDocument` today ([MainWindowViewModel.cs:94](../Argonaut/Shell/MainWindowViewModel.cs))
  and the status bar, find controller, and window title are all written against that
  singular assumption (see `IDocumentViewModel`'s own doc comment: "the shell only mirrors the
  current document's text"). `JsonDiffViewModel` looks like a precedent but isn't one at this
  layer — it's a *single* `IDocumentViewModel` that privately owns two mmap sessions (via
  `JsonDiffSession`) and renders two `ListBox`es itself; the shell still only ever swaps one
  `CurrentDocument`.
- Making this general means a real second-pane concept: two live sessions in memory
  simultaneously (real cost on the multi-GB files this app targets), a status bar and toolbar
  region that can address either pane, and find/search retargeting. This is shell-architecture
  work, not a Features-folder addition.
- **Cost: high**, and mostly orthogonal to the actual ask — the user wants provenance + a way
  back, not permanent two-up viewing. Worth keeping in mind if split-pane ever becomes a
  standalone feature (e.g. for diff-adjacent use cases), but oversized for this.

### C — Swap the current view, banner + back link (chosen)

`CurrentDocument` becomes a new `JsonArrayTableViewModel` (a real `IDocumentViewModel`, so it
gets a status bar line and a `Toolbar` for free); its `Toolbar` is a small view carrying "Table
view of `$.items` from `orders.json`" plus a **Back** command. Published exactly the way
`OpenDiffAsync` publishes a diff.

Two ways to build it, same shape, different cost:

- **C1 — independent sub-range session (build this).** Before the swap, capture the array's byte
  range from its token: `[startToken.Offset, endToken.Offset + endToken.Length)` where
  `endToken = index.GetToken(startToken.EndIndex)`. The table view then opens its **own**
  `IndexedFileSession<JsonStructureIndex>` over `new MMapFile(path, offset, length)` — the same
  machinery NDJSON already uses per line (item 2 above). Nothing is shared with the origin
  document, so `SetCurrentDocument`'s dispose-before-swap needs no change. Going Back calls the
  existing load path for JSON — a full reindex — then `NavigateToPathAsync(originPath)` once
  loaded, restoring scroll position exactly like a JSONPath search-and-reveal does today.
  **Honest cost note:** "only the array's subtree" is only cheap when the array is a small part
  of the file. The common shape for this feature — a document that *is* a top-level array of
  objects — means the table's own index is essentially a full reindex too. So C1 pays a reindex
  on the way in *and* on the way out. That is the same cost `SwitchViewAsync` already pays on
  every JSON⇄CSV⇄Raw switch, so it's not a new class of slowness the app doesn't already have,
  but the plan should not claim the entry hop is free.
- **C2 — keep the session alive (optimization, defer).** Hand the same `JsonStructureIndex`/
  `MMapFile` to the table view instead of disposing them, skipping both reindexes. Requires
  refcounting `IndexedFileSession` (or a "secondary view, don't dispose" flag in
  `SetCurrentDocument`) since two `IDocumentViewModel`s would reference one session — a real
  change to the lifetime contract every other view model currently relies on. Only worth doing
  if C1's reindexing proves annoying in practice on large files; ship C1 first and measure.

**Cost: low-to-medium.** New code is one `IDocumentViewModel` (`JsonArrayTableViewModel`), one
row-collection adapter (`JsonArrayRowCollection`) built from existing token-walk primitives, one
public shell entry point modelled on `OpenDiffAsync`, a `CsvStructure` extraction in the CSV
feature (see below), and a small banner/toolbar view — no shell-architecture changes, no touching
the tree's virtualization.

### D — Separate OS process, IPC-linked (expansion of C1)

Instead of swapping `CurrentDocument` in place, spawn a second Argonaut process for the table
view, launched with the file path plus new CLI args for view kind + position (e.g.
`--view=table --path=$.items`), so JSON and table are two real windows, each independently
positionable/monitor-placeable — a genuine desktop-multi-window benefit B doesn't have, since a
split pane is still one window. The "reload instead of opening a third view" half of the ask
needs actual IPC.

**What this reuses / costs, concretely:**

- **CLI parsing** — `App.axaml.cs:44-59` already takes positional path args and treats a second
  positional arg specially (diff mode); adding `--view=`/`--path=` dash-args and threading them
  to a "start in table mode at this path" call is small, same shape as the diff wiring.
- **Spawning** — `Process.Start` with this process's own executable path. Finding that path
  reliably across a macOS `.app` bundle, a Windows exe, and a Linux zip (README: "Linux still
  ships as a plain zip, no auto-update") is boilerplate but not hard
  (`Environment.ProcessPath`/`Process.GetCurrentProcess().MainModule`). Small.
- **"Kill and respawn" (no real IPC)** — if the parent process holds the `Process` handle it
  spawned itself, opening a second array-as-table node just needs a null-check-and-kill on that
  handle before spawning the next one. This is the user's "if a slave exists, kill it" framing,
  and it's cheap **only while one process always initiates and tracks the other** — no discovery
  problem, because nothing needs to find the child, the parent already has it. This does not,
  however, satisfy "tell the second process to reload" — it satisfies "replace the second
  process." Every retarget pays a fresh process start + fresh reindex of the array. **Cost: low**,
  but it's a strictly worse version of C1 with process-startup overhead added on top, unless the
  separate-window placement is itself the point.
- **Real IPC (reload in place)** — for the child to reload without a new process, the parent needs
  a channel to an already-running, possibly long-lived child: a named pipe (`System.IO.Pipes`,
  works on all three platforms) or local loopback socket, with the child listening from startup.
  This is new infrastructure with no precedent anywhere in the codebase (`grep` for
  IPC/pipe/socket/mutex across `Argonaut/` turns up nothing) and real edge cases to design for:
  child crashed or was closed by the user (parent's next message needs a
  connect-fails-then-respawn fallback, i.e. it still needs D's "kill and respawn" path as a
  backstop); child window brought to front vs. silently updated when the message arrives; what
  the child does with an in-flight index build if a second reload message arrives before the
  first finishes. **Cost: medium-high** — a real feature in its own right (bidirectional,
  crash-tolerant local IPC), not a small extension of C1.
- **Memory**: two OS processes means two full runtimes and two independent indexes in memory —
  strictly more resident memory than C1 (one process, one swap) or even B (one process, two
  sessions). `MemoryMappedFile` itself *can* be shared cross-process by name, but
  `JsonStructureIndex`'s token array is a private in-memory structure per `JsonStructureIndex.Build`
  call — sharing the mapped bytes doesn't avoid rebuilding it, so multi-process forecloses even
  C2's in-process session-sharing optimization. Every table open, first one or a reload, pays a
  full index build.
- **Platform risk**: macOS already has documented fragility around argv-driven multi-path opens —
  see `App.axaml.cs`'s own comments and its `startupArgPaths`/`OpenDebugLog` machinery, added
  specifically because macOS re-signals each launched path as a separate `FileActivatedEventArgs`
  race against argv parsing. A second-process-with-args flow sits exactly on that fault line and
  should expect to hit similar issues. Velopack auto-update (checks GitHub Releases on launch,
  per the README) is also untested against "many copies of this app launching each other" and
  wants a look before shipping this.

**Verdict**: D's kill-and-respawn variant is a strict cost/benefit downgrade from C1 (same
capability, worse latency, more moving parts) unless separate OS windows — for dragging to
another monitor/space independently of the JSON window — are themselves valued over C1's
single-window swap. D's real-IPC variant is a standalone feature (crash-tolerant local IPC
between app instances) that happens to be motivated by this use case, not a cheap expansion of
C1; size and schedule it as its own project if the separate-window benefit turns out to matter
enough to want reload-in-place. Not recommended as part of this plan.

### Outcome

Build **C1** — see [json-array-table-plan.md](json-array-table-plan.md) for the work itself.

It's the only option that doesn't either compromise the tree's virtualization
(A) or add a permanent multi-pane concept to the shell for a transient detour (B). It reuses
`MMapFile`'s sub-range mapping, `PublishDocument`, `NavigateToPathAsync`, `JsonRowFactory`'s
token-walk helpers, and the CSV grid's presentation types. Revisit C2 only if reindexing turns
out to matter for real files people hit this on. Skip D (separate process + IPC) for v1 — its
kill-and-respawn form is C1 with extra steps and worse latency, and its reload-in-place form is a
standalone IPC project, not a cheap expansion of this feature; see D's verdict above.

## 2. Who owns the mapping (C1 vs C2)

Covered inside option C above: C1 gives the table its own sub-range session and pays a reindex on
each hop; C2 shares the origin document's `IndexedFileSession` and needs the lifetime contract
relaxed (refcounting, or a "secondary view, don't dispose" flag in `SetCurrentDocument`). C1 ships
first. Revisit C2 only if the reindex proves annoying on real files — it is an optimization with a
real blast radius, and measuring beats guessing.

## 3. How the shell reveals a location

The problem: after Back reloads the origin file as JSON, something has to call
`NavigateToPathAsync(originPath)` on the newly current document, and the shell has no typed handle
on it. The same shape already exists for `JumpToRawOffsetAsync` (switch to Raw, then reveal a byte
offset).

### Rejected — a second concrete-type match

`await SwitchViewAsync(Json); if (CurrentDocument is JsonViewModel json) await json.NavigateToPathAsync(path);`
works and mirrors `JumpToRawOffsetAsync` exactly. Rejected because it does not extend: each new
document kind that wants revealing adds an arm, and [architecture.md](architecture.md) had gone out
of its way to record that the `RawViewModel` match was the shell's *only* one. Adding a second
would have turned a documented exception into a pattern.

### Chosen — capability interfaces

`IPathNavigable` / `IByteOffsetNavigable`, opted into by the documents that can honour them. The
shell asks "can this document do the thing", not "what is this document". It also lets
`JumpToRawOffsetAsync` drop its match, taking the shell from one concrete-type match to zero.

Worth recording the distinction, because architecture.md's original wording blurred it: that
document argued against putting `JumpToByteOffsetAsync` on `IDocumentViewModel` itself, which is
right — that interface is the surface *every* document shares. An opt-in capability interface is a
different thing. The codebase already models capabilities this way elsewhere
(`JsonToolbarViewModel.SupportsPathNavigation` is capability-by-injected-delegate).

### Rejected — reveal delegates in `DocumentViewCatalog`
 The catalog's
`Registrations` table already holds typed per-kind `Load` delegates with the cast inside, so a
sibling `Reveal` delegate would leave the shell with literally zero type tests and keep the
catalog as the app's single kind-to-view-model mapping. It doesn't work, because both reveal
flows are *switch if needed, then reveal on whatever is current*: `JumpToRawOffsetAsync` skips
the switch when already on Raw, and Back skips it when already on JSON. The reveal therefore
has to be addressable on a live, already-loaded document, which a load-time delegate cannot
reach — leaving the capability interface needed anyway, plus a redundant second mechanism.


## 4. How column widths get measured

`CsvColumnLayout` computes a character-count width per column once, from the header plus a sample
of rows, and then freezes it for the document's lifetime.

### Rejected — caching the sampled text

An early draft proposed keeping the decoded sample strings on the table view model so that changing
the reshape column count N could re-width from memory. Wrong on two counts: `Compute` consumes only
`row[c].Length` and never looks at the text, and retaining decoded rows adds a per-document
allocation the current code deliberately avoids (`CsvViewModel`'s `sampleRows` is a local, freed
once the layout is built). On pathological data — a minified document forced into a line-oriented
view, one array element holding a megabyte — retention is exactly where it hurts.

### Rejected — a two-row sample

The obvious objection is that two rows would do: the header for the name, one data row for the
values. That is the right answer *if measuring costs a decode per row* - which is what today's
`Compute(headerFields, sampleRows)` signature forces, and 249 rows of string building for a
heuristic that clamps at ~44 characters is not worth it. It stops being the right answer once
measuring is a length lookup (below), because then a wide sample is free and strictly better on
the case a narrow one gets wrong: a ragged column - `notes` empty on row 1, 200 characters on
row 40 - freezes at the 60px minimum and ellipsizes every value for the document's lifetime,
since widths are computed once and never revisited. So: make measuring free first, then sample
the whole batch. Do not keep the wide sample on top of a per-row decode.

### Chosen — free measurement, wide sample

`CsvFieldSpan.Length` (CSV) and `JsonTokenInfo.Length` (JSON) are already the numbers the heuristic
wants, available without building a string. Once measuring is free, sampling the whole
initial-paint batch costs an `int` comparison per element and is strictly better than a narrow
sample. See the plan's `CsvStructure` section for the resulting directives.
