# View a JSON array as a table — implementation plan

Let user pick a `[ { ... }, { ... } ]` node in the JSON tree and view it as a CSV-style grid,
reusing the CSV viewer's rendering, with a banner naming where it came from and a link back.

**Approach.** Picking "View as table" swaps `CurrentDocument` for a `JsonArrayTableViewModel` — a
real `IDocumentViewModel`, published the way a diff is — which opens its own independent session
over the array's byte range. Its toolbar carries the provenance banner, the column-mode dropdown,
and a Back command that reloads the origin file as JSON and navigates to the path it came from.

The options weighed on the way here (inline in the tree, a split pane, a second OS process; shared
vs. independent sessions; how the shell reveals a location; how widths are measured) are recorded
in [json-array-table-options.md](json-array-table-options.md) and are unchanged by the refactor
below. This document is the work.

> **Revision note (2026-08-29).** This plan was first written against the code as it stood on
> `optimize-json-diff-alignment`. Since then `1956b3a` ("Harden document and search lifetimes")
> introduced `IndexedDocumentViewModel`, `IDocumentSession`, `MemoryMappedCollectionBase` and
> `RequestTicket`, and `2768add` rewrote [architecture.md](architecture.md) to match. The feature
> got **smaller**: roughly half of stage 2's original checklist is now inherited from a base class.
> One structural consequence is new and load-bearing — see "The element index's task is a *session*
> concern now" below. Everything about the CSV side is unchanged: `CsvColumnLayout` and
> `CsvViewModel.LoadAsync` were not touched by the refactor, so that prerequisite stands verbatim.

## What already exists (and lowers cost)

1. **Programmatic navigation already ships.** `JsonViewModel.NavigateToPathAsync(string path)`
   ([JsonViewModel.cs:192](../Argonaut/Features/Json/JsonViewModel.cs)) resolves a JSONPath via
   `JsonPathResolver` and calls `SelectToken`, which expands ancestors and scrolls the tree to
   the token (`JsonVisibleRowCollection.EnsureVisible`). The "drive the JSON view back to where
   you came from" requirement is not new work — it is calling this with the path the table was
   opened from.
2. **Sub-range mapping and sub-range indexing already ship.** `MMapFile(string path, long offset,
   long length)` ([MMapFile.cs:46](../Argonaut/Infrastructure/MMapFile.cs)) maps just a byte range
   as its own independent OS-level mapping, decoupled from any other mapping over the same path;
   `JsonViewModel.LoadAsync(path, offset, length)`
   ([JsonViewModel.cs:286](../Argonaut/Features/Json/JsonViewModel.cs)) already builds a whole
   `IndexedFileSession<JsonStructureIndex>` over one, for the per-line NDJSON sub-documents.
   **This is the primitive the table view is built on**: the array's `[`…`]` byte range is a
   valid JSON document on its own, so the table view can own an independent session over exactly
   that range and never share state with the JSON view it came from. See stage 1.
3. **`IndexedDocumentViewModel` is now the document base class** (new since the first draft;
   [IndexedDocumentViewModel.cs](../Argonaut/Infrastructure/IndexedDocumentViewModel.cs)). It owns
   `FilePath`/`StatusText`/`IndexFailure`, `IndexingTask`, the indexing-completion monitor, the
   `IsDisposed` flag (volatile, safe to read off-thread), and — the point of it — the one correct
   `Dispose` ordering: `session.RequestStop()` → `MappedRows.Dispose()` → `DisposeCore()` →
   `session.Dispose()`. A subclass supplies two abstract members, `Session` and `MappedRows`, plus
   `Toolbar`, `CreateSearchNavigator()`, `CanHandleFileType(...)`, and reacts to the scan stopping
   through `OnIndexingCompleted()` / `OnIndexingFailed(failure)`. **The table view model is a
   subclass, and inherits all of that.** Stage 2 below is what remains after subtracting it.
4. **`IDocumentSession` is the lifetime seam** ([IDocumentSession.cs](../Argonaut/Infrastructure/IDocumentSession.cs)):
   `TearingDown` + `RequestStop()` + `Dispose()`, plus `IndexingTask` and `Failure`. Three
   implementations today — `IndexedFileSession<TIndex>`, `RawIndexSession`, and `JsonDiffSession`,
   the last of which composes two file sessions plus a *derived* index and reports the derived
   index's task as its own. That last one is the exact shape this feature needs (see stage 2).
5. **The shell already publishes documents that the catalog never built.**
   `MainWindowViewModel.OpenDiffAsync` ([MainWindowViewModel.cs:235](../Argonaut/Shell/MainWindowViewModel.cs))
   constructs a `JsonDiffViewModel` directly, loads it, and publishes it via `PublishDocument`
   with `FileTypeDetector.FileKind.Unknown`. It is the template for the table's entry point,
   *including* the pre-swap discipline the feature must not skip (see stage 3). Note
   `SetCurrentDocument` ([MainWindowViewModel.cs:594](../Argonaut/Shell/MainWindowViewModel.cs))
   is **private** — the entry point is a new public method on `MainWindowViewModel`.
6. **A view can already ask the shell to do something without referencing it.** `RawJumpService`
   ([RawJumpService.cs](../Argonaut/Infrastructure/RawJumpService.cs)) is a static event that
   `JsonView`'s "view in raw" link raises ([JsonView.axaml.cs:315](../Argonaut/Features/Json/JsonView.axaml.cs))
   and `MainWindow` alone subscribes to ([MainWindow.axaml.cs:51](../Argonaut/Shell/MainWindow.axaml.cs)).
   Per [architecture.md](architecture.md), `JsonView` never holds a reference back to
   `MainWindowViewModel`; the "View as table" action uses the same shape.
7. **The CSV grid is not reusable data-layer-first.** `CsvRowCollection` reads raw CSV-syntax
   bytes straight off `MMapFile` via `CsvFieldReader` — it has no concept of a JSON token. What's
   reusable is the *presentation* layer: `CsvView.axaml`, `CsvCell`, `CsvColumnLayout`, and the
   `CsvVisibleRow` shape. A JSON-array table needs a new `IList`-adapter (call it
   `JsonArrayRowCollection`) that reads `JsonStructureIndex` tokens the way
   `JsonRowFactory`/`DescribeChildCount` already do, and produces the same `CsvCell` shapes — not
   a rewire of `CsvRowCollection` itself. See "Extracting `CsvStructure`" for the one piece of the
   CSV layer that *is* worth generalising first.
8. **`MemoryMappedCollectionBase` is now the base for every virtualized ItemsSource**
   ([MemoryMappedCollectionBase.cs](../Argonaut/Infrastructure/MemoryMappedCollectionBase.cs)):
   subclasses implement only `GetCount()`, `GetItem(int)`, `DisposeCore()`, and the base
   short-circuits both to empty/null once disposed *before* calling them, so the
   report-empty-once-disposed rule cannot be forgotten. `JsonArrayRowCollection` subclasses it and
   writes none of that guard itself.
9. **Column discovery is a bounded token walk, not a DOM.** For each array element
   (`StartObject` token), walk its direct children only (skip nested containers via
   `EndIndex`, exactly like `JsonRowFactory.DescribeChildCount`
   ([JsonRowFactory.cs:142](../Argonaut/Features/Json/JsonRowFactory.cs))) to collect property
   names as columns, and decode each scalar via `JsonRowFactory.BuildScalarText`
   ([JsonRowFactory.cs:164](../Argonaut/Features/Json/JsonRowFactory.cs)). Both are *instance*
   methods on an `internal sealed` class constructed with `(index, mmap, hintProviders)` — the
   table view builds its own `JsonRowFactory` over its own session. Same O(children) cost model
   the tree view already pays per row — no new scanning primitive needed.
10. **A sparse "anchor every N" index already exists — and so does the base class it sits on.**
    `RawSegmentIndex` ([RawSegmentIndex.cs:48](../Argonaut/Features/Raw/RawSegmentIndex.cs)) stores
    one anchor per `AnchorStride` (64) display rows and resolves any row by bucketing
    (`rowIndex / AnchorStride`) then walking forward. Its load-bearing invariant: rows are
    published *only* at anchor boundaries, so every published row's bucket anchor is already
    visible to a lock-free reader. Its contents are byte-segmentation-specific and not reusable;
    its shape is, and `AppendLogIndexBase<T>`
    ([AppendLogIndexBase.cs](../Argonaut/Infrastructure/AppendLogIndexBase.cs)) is reusable
    outright — segmented append log, `ItemCount`, `IsComplete`, `Failure`, `WaitForCountAsync`,
    `OnItemsPublished`, `MarkComplete`, `RunIndexing`. **An index derived from another index,
    rather than built by scanning a file, also has a precedent**: `JsonDiffIndex`
    ([JsonDiffIndex.cs:82](../Argonaut/Features/Json/Diff/JsonDiffIndex.cs)) is an
    `AppendLogIndexBase` with its own `Start(leftIndex, leftFile, …)` factory
    ([JsonDiffIndex.cs:162](../Argonaut/Features/Json/Diff/JsonDiffIndex.cs)) and its own
    `IndexingTask` property, and deliberately *not* an `IFileIndexer` — so it never has to be the
    thing an `IndexedFileSession` starts. Stage 1's element index is those two shapes combined.
11. **Ownership friction (unchanged, and now written down).** `IndexedDocumentViewModel.Dispose`
    assumes the document solely owns its session, and `SetCurrentDocument` disposes the outgoing
    document *before* the swap. That contract is why the table view opens its own session rather
    than borrowing one, and why it pays a reindex on each hop (the alternative, and the conditions
    for revisiting it, are in [json-array-table-options.md](json-array-table-options.md) §2).
    **The table view must not hold the origin document's `JsonStructureIndex` or `MMapFile`** —
    both are disposed before the table is published, and reading them afterwards is an
    `ObjectDisposedException` at best (`MMapFile.GetSpan` guards) and a stale-read at worst.

## What the refactor changed about this plan

- **Stage 2 shrinks.** The original stage 2 enumerated `FilePath`, `IndexingTask`, `IndexFailure` +
  `StatusText`, `WindowTitle` and the `MonitorIndexingAsync` shape as things to write. All are the
  base class's now. What is left to write is the load, the toolbar, the two completion hooks, and
  three one-liners (`Session`, `MappedRows`, `CanHandleFileType => false`).
- **The element index's task is a *session* concern now.** The original plan said "`IndexingTask`
  — the **element index's**, not the session's", because the element index publishes one final
  stride after the token scan completes, and handing the shell the token scan's task makes progress
  stop one stride early. `IndexedDocumentViewModel.IndexingTask` is **non-virtual** (`Session?.IndexingTask
  ?? Task.CompletedTask`) — deliberately, so no subclass can route around the monitor. So the
  difference is expressed one layer down, in a small `JsonArrayTableSession : IDocumentSession`
  that owns the inner `IndexedFileSession<JsonStructureIndex>` plus the `JsonArrayElementIndex` and
  reports the element index's task as its own. This is not a workaround: it is exactly what
  `JsonDiffSession` does (`IndexingTask => Diff.IndexingTask`, with the two file sessions inside),
  and it puts the "which task is this document actually waiting on" answer in the type that owns
  both tasks rather than in the view model. **Net: one new ~80-line type, and a whole class of
  ordering bugs the old plan would have had to hand-write in the view model.**
- **The disposal checklist disappears.** No `Dispose()` to write on the view model at all: the base
  sequences stop → rows → `DisposeCore()` → session, and `JsonArrayTableSession.Dispose` sequences
  cancel → join element index → dispose inner session (`JsonDiffSession`'s ordering, minus a side).
- **`DetachFindAsync` is now `DetachFind()`, synchronous.** Stage 3's `await DetachFindAsync()`
  becomes `DetachFind()`; search safety no longer depends on it, because each scan owns its own
  chunk mappings (see architecture.md, "Search interaction"). It is still called, for UI hygiene,
  in the same position as `OpenDiffAsync` does.
- **Staleness uses `RequestTicket`.** `var requestId = openRequest.Begin();` … `if
  (!openRequest.IsCurrent(requestId))`, not a hand-rolled `++openRequestId`.
- **The NDJSON offset trap is cheaper to fix if we ever want to.** `JsonViewModel` now records
  `internal ScanTarget ScanTarget` ([JsonViewModel.cs:41](../Argonaut/Features/Json/JsonViewModel.cs)),
  and the sub-range overload sets it to `new ScanTarget(path, offset, length)` — so the mapping's
  base offset is already stored, and no new `MappingBaseOffset` field is needed. v1 still restricts
  "View as table" to the shell-level JSON document (see stage 3), but the escape hatch is now one
  addition, not a new field on a class.
- **Nothing else moved.** `CsvColumnLayout`, `CsvViewModel`, `CsvRowCollection`, `JsonRowFactory`,
  `JsonStructureIndex`, `JsonPathResolver`, `RawSegmentIndex`, `AppendLogIndexBase`,
  `RawJumpService` and the `MainWindow.axaml` template block are all as the first draft described.
  `CsvRowCollection` still runs its own bespoke `DispatcherTimer` rather than
  `IndexGrowthMonitor` — leave that alone; the new collection uses `IndexGrowthMonitor`.

## Commit order

Two of the pieces below are refactors of working code that the table feature needs but does not
own. They land first, separately, so the feature diff is the feature and each refactor can be
reviewed (and reverted) on its own terms.

1. **Capability interfaces.** Add `IPathNavigable` / `IByteOffsetNavigable` (stage 4), declare them
   on `JsonViewModel` / `RawViewModel`, convert `JumpToRawOffsetAsync`
   ([MainWindowViewModel.cs:186](../Argonaut/Shell/MainWindowViewModel.cs)) to query the capability
   instead of matching `RawViewModel`, and update [architecture.md](architecture.md) — both the
   `IDocumentViewModel` bullet ("the shell reaches into a document view model for **nothing**
   except `JumpToRawOffsetAsync`'s `RawViewModel` match") and the failure-banner bullet ("the
   shell's only such match") — to the rule that then holds. Two lines of shell change plus two
   interface declarations; no behaviour change, existing tests cover it. Doing this first means
   the table's Back consumes a mechanism that already exists rather than introducing one.
   Fold one documentation fix in here: `JsonTokenInfo.Offset`'s XML comment reads *"Absolute byte
   offset in the file"* ([JsonStructureIndex.cs:32](../Argonaut/Features/Json/JsonStructureIndex.cs)),
   which is false for a sub-range mapping — and is exactly the trap stage 3 warns about. Same for
   `NameOffset` on line 36.
2. **`CsvStructure` extraction.** Replace `CsvColumnLayout` with `CsvStructure` (names + widths +
   prebuilt `HeaderCells`), move measurement from decoded `string[]` rows to spans, and add
   `SetStructure` to `CsvRowCollection`. Touches `CsvColumnLayout.cs`, `CsvRowCollection.cs`,
   `CsvViewModel.cs`, `CsvView.axaml.cs`, `CsvColumnLayoutTests.cs`, `CsvRowCollectionTests.cs`.
   Standalone value regardless of this feature: it removes a per-open decode of up to 250 rows
   from the CSV/TSV load path ([CsvViewModel.cs:134-140](../Argonaut/Features/Csv/CsvViewModel.cs)).
3. **`JsonArrayElementIndex`** (stage 1, part A). Pure data layer: an `AppendLogIndexBase<int>`
   over an existing `JsonStructureIndex`, owning no session and no mapping. Lands on its own
   because every correctness hazard in this feature — element addressing, growth, open-container
   `EndIndex` — is inside it, and all of it is testable against a hand-built index with no UI.
4. **`JsonArrayTableSession`** (stage 2, part A). The `IDocumentSession` that composes the
   sub-range file session with the element index. Small, and testable the way
   `JsonDiffSessionTests` tests its analogue — start it over a temp file, assert
   `IndexingTask` is the element index's, assert idempotent `Dispose`, assert `TearingDown` fires
   from either direction.
5. **`JsonArrayRowCollection` + `JsonArrayTableViewModel`** (stage 1 part B, stage 2 part B). Pure
   new code in `Features/Json`, testable without the shell — a `CsvStructure` and a sub-range
   session are both constructible in a test. Add the new collection to
   `CollectionDisposedEmptyTests` and the new document to `DocumentDisposalLifecycleTests`; those
   two suites are where the refactor's invariants are pinned, and a new document/collection that
   isn't in them is untested against the exact bugs the base classes exist to prevent.
6. **Entry point + Back** (stages 3-4). The `ArrayTableService` event, `MainWindow`'s
   subscription, and `MainWindowViewModel.OpenArrayTableAsync`.
7. **View + shell templates** (stages 5-6). `JsonArrayTableView.axaml` + code-behind, the toolbar
   view, and the two `DataTemplate` registrations.

Steps 1 and 2 are independent of each other and of the decision to build this feature at all;
steps 3-7 are the feature.

## Extracting `CsvStructure` (prerequisite refactor)

Column *identity* cannot come from the CSV layer for this feature: it is either the property
names discovered by the JSON token walk, or `Column 1..N` placeholders chosen by a UI control.
Either way the names and their widths are computed elsewhere and handed to something that renders
a CSV-shaped grid. Today that information is split in two and half of it is private to
`CsvViewModel`:

- `CsvColumnLayout` ([CsvColumnLayout.cs:39](../Argonaut/Features/Csv/CsvColumnLayout.cs)) holds
  only widths. `Compute(headerFields, sampleRows)` does *not* discover columns — it takes the
  column count from `headerFields.Count` and computes a character-count width per column.
- The names live in `CsvViewModel.headerFields`, and the placeholder fallback is
  `UpdateHeaderCells` ([CsvViewModel.cs:184](../Argonaut/Features/Csv/CsvViewModel.cs)), which
  already synthesizes `"Column {c + 1}"` when "first row is header" is off. That is exactly the
  placeholder-name case the JSON reshape mode needs, currently unreachable from outside CSV.

**Refactor**: replace `CsvColumnLayout` with `CsvStructure` — the grid's shape as one immutable
object, constructed by whoever knows the data, consumed by whoever renders it.

```csharp
/// One column: display name plus the fixed pixel width its cells render at.
public readonly record struct CsvColumn(string Name, double Width);

public sealed class CsvStructure
{
    public IReadOnlyList<CsvColumn> Columns { get; }
    public int ColumnCount { get; }
    public double TotalWidth { get; }
    /// Prebuilt header row - the same CsvCell shape the data rows use.
    public IReadOnlyList<CsvCell> HeaderCells { get; }
    public double WidthFor(int columnIndex);   // min-width fallback past ColumnCount, as today

    /// The one core factory: per-column pixel widths from a per-column maximum character
    /// count. Every caller measures in whatever currency is cheapest for its own data and
    /// hands the counts in - nothing in here ever sees text.
    public static CsvStructure FromMaxChars(IReadOnlyList<string> names, ReadOnlySpan<int> maxChars);
}
```

**One factory, three measurers.** Names and per-column character counts are computed by whoever
knows the data; `CsvStructure` discovers nothing:

- **CSV/TSV**: names from the header line (or `"Column N"`), counts from `CsvFieldSpan.Length`
  over the sampled rows — see "Measure from spans" below. That length is raw bytes *including* the
  field's surrounding quotes and its doubled `""` escapes, so it over-counts; harmless for the
  same reason the UTF-8 over-count is (the clamp saturates), but it is not a character count.
- **JSON by-property**: names are the discovered property names; each column's count is the
  length of that column's *child value* token across the sampled elements, seeded by the property
  name's own length so the header always fits. **Not the element's own token length** — an array
  element is a `StartObject`, whose recorded `Length` is `ValueSpan.Length` = 1, the brace itself.
- **JSON reshape**: names are `"Column 1".."Column N"`, and here the element's own token `Length`
  *is* the right measure, because each cell is exactly one element. (Reshaping an array of
  objects is the exception — those cells show a container summary like `{ 3 members }` — but that
  is already the degenerate use of the mode, and a min-width column is the honest result.)

A `sealed class`, not a `struct`: it holds an array either way (so nothing is saved by making the
wrapper a value type), it is allocated once per shape change rather than per row, and an immutable
class avoids the defensive-copy and mutable-value-type traps a `struct` with an array field
invites. `CsvColumn` itself is a `readonly record struct`, mirroring the existing `CsvCell`.

**Who holds it**: the row collection, not just the view model. `JsonArrayRowCollection` takes a
`CsvStructure` and reads `ColumnCount` from it to decide how to chunk the array in reshape mode;
`CsvRowCollection` takes one too (it already takes the layout —
[CsvRowCollection.cs:46](../Argonaut/Features/Csv/CsvRowCollection.cs)) and uses it only for cell
widths, since its chunking is per file line. **Keep the chunk *rule* in the row collection, not in
`CsvStructure`** — the rule differs per source (CSV: one line per row; JSON by-property: one array
element per row; JSON reshape: N elements per row) and only the column *count* is shared input.

**Changing shape**: a new `CsvStructure` replaces the old one via `SetStructure(CsvStructure)` on
the row collection, which clears the LRU cache and raises `NotifyCollectionChangedAction.Reset` —
precisely what `CsvRowCollection.SetDataStartIndex`
([CsvRowCollection.cs:103](../Argonaut/Features/Csv/CsvRowCollection.cs)) already does for the
header tickbox. The view model then raises `PropertyChanged` for the structure so the sticky header
rebinds. Note widths are baked into each `CsvCell` at realization time, so both halves are needed:
cache-clear + Reset for the data rows, and a new `HeaderCells` for the header.

**Cost of a shape change**: widths come from a sample, so a new N needs re-widthing. This needs
neither a cache nor a decode. Three facts make it nearly free:

- `Compute` consumes only `row[c].Length`. It never looks at the text.
- `JsonTokenInfo.Length` is already that number — the token's content byte length, unpacked O(1)
  from the packed token log with no read of the mapping at all. (Byte length over-counts
  multi-byte UTF-8 slightly; irrelevant against a heuristic that clamps.)
- The formula saturates: `Math.Clamp(maxChars * 7.0 + 16.0, 60.0, 320.0)` gives the max width at
  or past ~44 characters and the min at or below ~6, so the over-count and the sample size both
  stop mattering quickly.

So re-widthing for a new N is a walk over the sampled elements' `token.Length` values, bucketed by
`i % N`. No file read, no strings, no retained side array. **Do not cache decoded sample text** —
`CsvViewModel` deliberately doesn't (its `sampleRows` is a local, freed once the layout is built),
and on pathological data — a minified document forced into a line-oriented view, one array element
holding a megabyte — retention is exactly where it hurts.

**Sample size**: reuse the initial-paint batch the load already awaits, as `CsvViewModel` does
with `InitialIndexedRowTarget` (250) — those elements are already indexed by the time the first
frame needs them, so this is not a separate read. On the JSON side the sample is whatever
`ElementCount` has published by then, which is a multiple of `ElementStride` until the array
completes — so "250" is a ceiling, not a promise, and the sample loop must read `ElementCount`
rather than assume it. Widths are a saturating heuristic either way; 192 sampled elements and 250
give the same answer on any real data. This is only correct once measuring is free (see directly
below); a wide sample on top of a per-row decode is the one combination to avoid. A narrow two-row
sample and a cached-text alternative were both considered and rejected — see
[json-array-table-options.md](json-array-table-options.md) §4.

**Measure from spans, not strings** (do this as part of the refactor — it is what justifies the
sample size above): `CsvColumnLayout.Compute` currently takes decoded `string[]` rows, which is
why `CsvViewModel.LoadAsync` decodes up to 250 rows purely to measure them. `CsvFieldReader.SplitToSpans`
already returns `CsvFieldSpan(Offset, Length)`, so measuring from those spans deletes that decode
outright — and `Compute` is being replaced by `FromMaxChars` by this refactor regardless, so the
signature change is not extra cost. The JSON table never had the decode to begin with:
`JsonTokenInfo.Length` is the measurement, taken from whichever token the mode makes a cell.

**Blast radius**: `CsvColumnLayout.cs`, `CsvRowCollection.cs`, `CsvViewModel.cs`,
`CsvView.axaml.cs` (`ScrollColumnIntoView` takes the layout), plus `CsvColumnLayoutTests.cs` and
`CsvRowCollectionTests.cs`. Small and mechanical; `CsvView.axaml` itself binds only `HeaderCells`
and `Rows`, so the markup is unaffected. Do this first, as its own commit, so the JSON table
consumes a shape that already exists rather than growing a parallel one.

## Stages

### 1. Element addressing, then rows

**Part A — `JsonArrayElementIndex`.** This is the only place in the feature where "a JSON array is
not a line-oriented file" lives, so it is worth isolating. `CsvRowCollection` can serve row *i*
because `Count` is `index.LineCount - dataStartIndex`
([CsvRowCollection.cs:59](../Argonaut/Features/Csv/CsvRowCollection.cs)) and a line index is a
dense array. A JSON array's elements are neither: finding the *i*-th direct child of the root
array means skipping every preceding element's whole subtree via `EndIndex`. Left in the row
collection that is O(i) per realized row plus a full re-walk on every growth tick. So it moves
into an index of its own, built from the two shapes that already exist (see §10 above):
`RawSegmentIndex`'s sparse anchors and `JsonDiffIndex`'s derived-index lifecycle.

```csharp
public sealed class JsonArrayElementIndex : AppendLogIndexBase<int>
{
    /// Elements per stored anchor. Deliberately the same number as
    /// RawSegmentIndex.AnchorStride - no reason for this codebase to hold two answers to
    /// the same RAM/rescan trade.
    internal const int ElementStride = 64;

    public static JsonArrayElementIndex Start(JsonStructureIndex index, int arrayTokenIndex,
                                              CancellationToken cancellationToken);

    /// Not an IFileIndexer - it scans no file. Same call the diff index makes, and for the
    /// same reason: it must never be the thing an IndexedFileSession starts.
    public Task IndexingTask { get; }

    /// Elements whose own token has closed AND whose bucket anchor is published; grows.
    public int ElementCount { get; }

    /// Token index of element i: one bucket lookup, then at most ElementStride - 1
    /// EndIndex hops. No allocation, no file read.
    public int TokenForElement(int elementIndex);

    /// Waits for a target element count - the initial-paint wait, in the element coordinate
    /// system rather than the token one. Thin wrapper over the base's WaitForCountAsync,
    /// mirroring JsonStructureIndex.WaitForTokenCountAsync / JsonDiffIndex.WaitForRecordCountAsync.
    public Task WaitForElementCountAsync(int targetCount);
}
```

One `int` per 64 elements is **640KB on a ten-million-element array**, against 40MB for a dense
per-element map. What it costs is at most 63 `EndIndex` hops per lookup, each an O(1) unpack from
the packed token log — unmeasurable inside a frame, and the reason there is deliberately **no "all
elements are scalars, so element *i* is token *i+1*" fast path**. That special case would be
correct, but it doubles the states the walk can be in to save time nobody can see.

Note `AppendLogIndexBase<T>` requires `T : struct`, and `int` satisfies it; anchors are read with
`this.items.ItemRef(bucket)`, exactly as `RawSegmentIndex.GetRowInfo` does
([RawSegmentIndex.cs:93](../Argonaut/Features/Raw/RawSegmentIndex.cs)). The scan body goes through
`RunIndexing(...)` so a fault is recorded as `Failure` and `MarkComplete` still runs.

Two rules it must not get wrong:

- **Advance only over closed elements.** Skipping a container is `i = t.EndIndex + 1`
  ([JsonRowFactory.cs:155](../Argonaut/Features/Json/JsonRowFactory.cs)), and `EndIndex` is
  `-1` until that container closes — so on an open element that expression is `i = 0` and the
  walk spins forever. `JsonRowFactory.DescribeChildCount` is safe only because it never runs
  before its container's `EndIndex` is known; here the *root array is open for the entire
  scan*, so the stopping rule is "the last element whose own `EndIndex` resolved", never
  `index.TokenCount`.
- **Stream — do not wait for the source index.** `JsonDiffIndex` blocks on both source indexes
  completing because a half-scanned diff is meaningless. A half-scanned array is a perfectly good
  table of the elements so far, so this one consumes tokens incrementally via
  `JsonStructureIndex.WaitForTokenCountAsync` (the coverage-wait loop at
  [JsonPathResolver.cs:132](../Argonaut/Features/Json/JsonPathResolver.cs)) and publishes at
  stride boundaries — `RawSegmentIndex`'s invariant, so every published element's bucket anchor is
  already visible to a lock-free reader. **And once more at completion**, exactly as
  `RawSegmentIndex` does: without that final publish an array of fewer than `ElementStride`
  elements never crosses a boundary and the table stays empty forever. The common small-array case
  is the one this rule exists for.

**Part B — `JsonArrayRowCollection`**, a `MemoryMappedCollectionBase` subclass over the table
view's **own** session, *never* over the origin document's index or mapping. Given
`(JsonArrayElementIndex, JsonRowFactory, CsvStructure, mode)`, lazily produce `CsvVisibleRow`s,
mirroring `CsvRowCollection.GetRow`'s on-demand-plus-LRU-cache shape for realization. It keeps no
walk state of its own: `GetCount()` derives from `ElementCount`, and realizing a row is a
`TokenForElement` lookup per element the row covers — one in by-property mode (plus a bounded read
of that element's direct children), `N` in reshape mode — and nothing else. It implements only
`GetCount()`, `GetItem(int)` and `DisposeCore()`; the empty-once-disposed guard is the base's.
Growth tracks the element index through `IndexGrowthMonitor`
([IndexGrowthMonitor.cs](../Argonaut/Infrastructure/IndexGrowthMonitor.cs)) — constructed with
`(interval, elementIndex.IndexingTask, () => elementIndex.IsComplete, refresh)`, as
`JsonVisibleRowCollection` and `JsonDiffRowCollection` both do — rather than re-deriving CSV's
bespoke `DispatcherTimer`. Non-object elements (scalars, nested arrays) get a single "value"
column — don't special-case ragged arrays beyond that. Column discovery itself (union or
first-N-sample of property names, order, dedupe) happens in stage 2 and arrives as a finished
`CsvStructure`; this collection never invents columns.

Cell text is `JsonRowFactory.BuildScalarText` **unchanged, quotes included**: `"5"` and `5` are
different data, and a table that hides the difference is lying about the document. Same text the
tree shows for the same token, so the two views never disagree. (A string past
`DisplayText.MaxLength` keeps its opening quote and gets no closing one — deliberate, see that
method's comment; a copy-cell action, if one is ever added, inherits that convention rather than
re-deriving it.)

**Column mode: by-property vs. flattened N-way reshape.** Some flat arrays are really
n-dimensional data flattened into one sequence (interleaved coordinates, RGB triples, a
fixed-width record repeated with no object wrapper) — `[x0, y0, x1, y1, ...]` rather than
`[{x, y}, {x, y}, ...]`. Give the table a second column mode alongside "by property": **reshape
into N columns**, N chosen 1-5 from a toolbar dropdown. Element `i` of the flat array goes to
`Rows[i / N]`, `Column[i % N]` — row-major, same walk order as the array itself, so it's a pure
re-chunking of `JsonArrayRowCollection`'s existing per-element decode, no new value-reading path.
`Count` becomes `ceil(ElementCount / N)` — still growing while the element index does; header
cells are the generic `Column 1..N` names carried by the `CsvStructure` (the same labels
`CsvViewModel.UpdateHeaderCells` synthesizes for a headerless CSV today).

A partial last row simply yields fewer `CsvCell`s than the header has columns, and the row's
`ItemsControl` renders that many cells — each cell carries its own width, so nothing misaligns; no
padding and no special case are required. (This is *not* the same thing as `CsvRowCollection.GetRow`'s
empty-row fallback, which covers an out-of-range **row** index.)

This mode has no correct/incorrect N to validate against — the whole point is the user is telling
the view something about the data's shape that the JSON itself doesn't encode. Picking a wrong N
is not an error case: it just produces a table with values that don't line up column-to-column
across rows. No warning, no rejection — surfacing that mismatch is on the user, since there's no
signal in the data to distinguish "wrong N" from "ragged input" from "intentional." Available for
any array regardless of element shape — an array of objects can be reshaped too (each cell shows
that element's container summary), it's just less likely to be what someone wants there.

### 2. The session, then the document

**Part A — `JsonArrayTableSession : IDocumentSession`.** Modelled on `JsonDiffSession`
([JsonDiffSession.cs](../Argonaut/Features/Json/Diff/JsonDiffSession.cs)), one side instead of two:

```csharp
public sealed class JsonArrayTableSession : IDocumentSession
{
    public IndexedFileSession<JsonStructureIndex> Inner { get; }
    public JsonArrayElementIndex Elements { get; }

    /// Linked from Inner.TearingDown, so teardown started at either end fires both.
    public CancellationToken TearingDown => elementCts.Token;

    /// The ELEMENT index's task, not the token scan's - the token scan completing is not the
    /// moment the table stops growing (one final stride publish follows it), so handing the
    /// shell the inner task makes progress reporting stop one stride early. Exactly why
    /// JsonDiffSession reports the diff's task rather than either side's.
    public Task IndexingTask => Elements.IndexingTask;

    /// The token scan's failure first - that is the one a malformed range produces. The
    /// element index's own is a fallback (it scans no file, so a fault there is a defect,
    /// but reporting it beats swallowing it).
    public IndexFailure? Failure => Inner.Failure ?? Elements.Failure;

    public static JsonArrayTableSession Start(string path, long offset, long length,
                                              IProgressReporter? progress = null);
}
```

`Start` maps the range (`new MMapFile(path, offset, length)`), starts
`IndexedFileSession<JsonStructureIndex>.Start(mmap, JsonStructureIndex.StartIndexing, progress)`,
then starts `JsonArrayElementIndex.Start(inner.Index, arrayTokenIndex: 0, elementCts.Token)` —
**root token 0 is always the array**, because the table's mapping *is* the array. On any throw
after the first step, dispose what was started before rethrowing (`JsonDiffSession.Start`'s
try/catch shape).

`Dispose` ordering, non-negotiable and the reason this type exists:

1. `RequestStop()` — cancel `elementCts` (linked, so the inner session stops too) and
   `Inner.RequestStop()`.
2. Join `Elements.IndexingTask` — after this nothing reads the inner index.
3. `Inner.Dispose()` — which cancels, joins its own scan and dependents, and releases the mapping.
4. Dispose `elementCts`.

Idempotent, UI-thread-only, same contract as its two siblings.

**Part B — `JsonArrayTableViewModel : IndexedDocumentViewModel`.** Constructed with no arguments
(like every other document VM); `LoadAsync(string filePath, long arrayOffset, long arrayLength,
string originPath)` does the work:

- set `FilePath = filePath` (the origin file's path — the banner shows the range, not a synthetic
  name);
- `session = JsonArrayTableSession.Start(filePath, arrayOffset, arrayLength, progress)`, where
  `progress` is a `ProgressToStatus`-style private reporter, copied from
  `JsonDiffViewModel`'s ([JsonDiffViewModel.cs:441](../Argonaut/Features/Json/Diff/JsonDiffViewModel.cs)):
  `Dispatcher.UIThread.Post`, ~5% buckets, silent once `IsDisposed`. **This is the answer to the
  first draft's dangling "either create a `StatusProgressReporter` … or drop this sentence"**: the
  diff already established that a directly-published document reports its own progress rather than
  borrowing the shell's private reporter, and the entry point just silences the outgoing one.
- `await session.Elements.WaitForElementCountAsync(InitialElementTarget)` — the initial-paint wait,
  the way `JsonViewModel.LoadCore` and `JsonDiffViewModel.LoadAsync` both do. Then
  `if (IsDisposed) return;` (base-class flag).
- surface an immediate `session.Failure` into `IndexFailure`, as `JsonViewModel.LoadCore` does — a
  malformed range shows up as a normal partial/failed index, and a failure with `ItemsIndexed == 0`
  lands in the incompatible placeholder through the shell's existing path.
- build the `JsonRowFactory` over `session.Inner.Index` / `session.Inner.File`, sample the first N
  elements for the initial `CsvStructure` (widths from token lengths — the per-column *child value*
  tokens in by-property mode, the element tokens in reshape mode; no decode, nothing retained),
  and create `rows`.
- build the toolbar, set `StatusText`, and call **`MonitorIndexing()` before returning** — the base
  class requires it, and `StopProgressWhenIndexedAsync`'s ordering guarantee depends on it.

Members that remain to write, now that the base owns the rest:

- `protected override IDocumentSession? Session => session;`
- `protected override IDisposable? MappedRows => rows;`
- `public override object? Toolbar => toolbar;`
- `public override bool CanHandleFileType(...) => false` — never reached via `DocumentViewCatalog`,
  only constructed directly, exactly like `JsonDiffViewModel`.
- `public override ISearchNavigator? CreateSearchNavigator() => null` for v1 — find is disabled
  while in table view. A `CsvSearchNavigator` analogue over token offsets is separate work; null is
  the contract's supported answer and the shell already handles it (`IsFindAvailable`). Note this
  also sidesteps `ISearchNavigator.DocumentTearingDown`, which deliberately has no default
  implementation.
- `OnIndexingCompleted()` / `OnIndexingFailed(failure)` — final row count, or the stopped-early
  text, in `CsvViewModel`'s wording.
- `WindowTitle` — leave the interface default (null); the banner carries the provenance.

Not written, because the base owns them: `Dispose`, `IndexingTask`, `FilePath`/`StatusText`/
`IndexFailure` storage and notification, and the indexing-completion monitor loop.

**Toolbar** — a `JsonArrayTableToolbarViewModel` exposing the origin path/file text, the
column-mode dropdown (by-property / reshape 1-5), and `BackAsync`. Changing the mode builds a new
`CsvStructure` and calls `SetStructure` on the row collection (cache clear + Reset); it does not
re-walk the array. **The picker is a plain toolbar `ComboBox`, not a flyout that closes on pick.**
The mode setter is selection-bound, so it runs inside Avalonia's still-open selection commit (see
CLAUDE.md): the `Reset` it causes is safe *only* because it lands on the grid's collection, never
on the picker's own `ItemsSource`. Put the same setter behind a self-closing flyout and it is the
`SchemaRootPickerViewModel.SelectedPick` crash verbatim — at which point it owes
`UiDeferral.AfterCurrentInput`.

### 3. Entry point

A row-level action on array nodes in `JsonView`/`JsonRowPresenter` ("View as table"), raised
through a static `ArrayTableService`-style event in the shape of `RawJumpService`, with
`MainWindow` as sole subscriber; `JsonView` gets no reference to the shell. Note the row's
right-click gesture is already taken (copy value, `JsonView.axaml.cs:370`), so this wants its own
affordance on container rows — the "view in raw" link and the schema/hint links are the precedent
for a small inline button that only appears on rows that qualify.

The JSON view resolves, *before* raising: the array's `startToken`, its
`endToken = index.GetToken(startToken.EndIndex)`, the byte range
`[startToken.Offset, endToken.Offset + endToken.Length)` — a `StartArray`/`EndArray` token records
its offset at the bracket with `Length` 1, so that closing term is `+1`, and an off-by-one here
surfaces as a `JsonReaderException` out of the table's own indexer rather than as anything legible
— and `JsonPathBuilder.Build(...)` for the origin path.

**Offsets are mapping-relative, not file-relative.** `JsonStructureIndex` records each token's
offset relative to the `MMapFile` it indexed. That equals the file offset for a normally-opened
JSON document (mapped from 0), but *not* for the per-line `JsonViewModel` instances NDJSON nests,
which are already sub-range mappings. **v1 restricts "View as table" to the shell-level JSON
document.** If that restriction is ever lifted, the base offset is now already recorded —
`JsonViewModel.ScanTarget.Offset` ([JsonViewModel.cs:41](../Argonaut/Features/Json/JsonViewModel.cs),
set by the sub-range `LoadAsync` overload) — so it is an addition at the raise site, not a new
field.

**Indexing race**: `EndIndex` is `-1` until the container closes, so on a still-indexing file the
action must wait for it (the `WaitForEndIndexAsync` pattern at
[JsonPathResolver.cs:132](../Argonaut/Features/Json/JsonPathResolver.cs)) or be disabled until
then. "Enabled for any array with at least one element" is not a sufficient guard. That method is
`private static`, so this is a decision rather than a reference: widen it to `internal static`
(preferred — one implementation of the wait, and `JsonArrayElementIndex` wants the same loop) or
reimplement it at the entry point.

The shell handler is a new **public** `MainWindowViewModel.OpenArrayTableAsync(string path,
long offset, long length, string originPath)`, modelled line-for-line on `OpenDiffAsync`
([MainWindowViewModel.cs:235](../Argonaut/Shell/MainWindowViewModel.cs)) — none of these steps are
optional:

- `var requestId = openRequest.Begin();`
- `DetachFind();` (synchronous now) and `FindBarResetRequested?.Invoke();`
- `indexProgressReporter?.Stop();` — the table reports its own progress, so the shell's outgoing
  reporter must go quiet and stay quiet.
- load, catching and logging via `OpenDebugLog`; dispose the document if it throws, and dispose it
  again if a newer request won the race (`!openRequest.IsCurrent(requestId)`).
- `PublishDocument(document, path, FileTypeDetector.FileKind.Unknown, addToRecents: false);`

**`FileKind.Unknown` is deliberate**, same as diff: the view switcher shows no selection for the
table, and picking any view there re-indexes the origin file as that kind through the normal
switch path — a free second route back. Publishing as `FileKind.Json` instead would make
`SwitchViewAsync` no-op on `kind == currentKind` and strand the user on the banner link.

### 4. Back

Reload the origin path as JSON via the existing switch path, then navigate to `originPath`.
Neither `SwitchViewAsync` ([MainWindowViewModel.cs:430](../Argonaut/Shell/MainWindowViewModel.cs))
nor `LoadAndPublishAsync` ([MainWindowViewModel.cs:455](../Argonaut/Shell/MainWindowViewModel.cs))
has a post-publish hook today, and the shell has no typed handle on the document it just
published, so this needs a way to say "reveal this address on the current document".

**Do it with a capability interface, not a concrete-type match:**

```csharp
/// A document that can reveal a JSONPath - implemented by JsonViewModel today, and by
/// NdJsonViewModel or any future tree-shaped view without the shell changing.
public interface IPathNavigable { Task NavigateToPathAsync(string path); }

/// A document that can reveal a byte offset - implemented by RawViewModel.
public interface IByteOffsetNavigable { Task JumpToByteOffsetAsync(long byteOffset); }
```

Both members already exist with exactly these signatures
([JsonViewModel.cs:192](../Argonaut/Features/Json/JsonViewModel.cs),
`RawViewModel.JumpToByteOffsetAsync`), so this is a declaration on each class and no new method
bodies. Back becomes:

```csharp
await SwitchViewAsync(FileTypeDetector.FileKind.Json);
if (CurrentDocument is IPathNavigable nav)
    await nav.NavigateToPathAsync(originPath);
```

This is a capability query, not a type switch: nothing here grows a `case JsonViewModel:` arm as
views are added, and a new document supporting path reveal implements the interface with no shell
edit.

**`BackAsync` outlives its own view model.** `SwitchViewAsync` reaches `SetCurrentDocument`, which
disposes the outgoing document *before* the swap
([MainWindowViewModel.cs:599](../Argonaut/Shell/MainWindowViewModel.cs)) — and the outgoing
document is the table whose toolbar raised this. So everything after that first `await` runs on a
disposed object: it may touch locals and already-captured strings (`originPath` is one) and
nothing else — never `session`, `Rows`, or the element index. The safest shape is for the command
to live on the toolbar view model with `originPath` captured at construction, so the disposed
document is not on the path at all.

**It also retires the existing match rather than adding a second.** `JumpToRawOffsetAsync`
([MainWindowViewModel.cs:186](../Argonaut/Shell/MainWindowViewModel.cs)) becomes
`if (CurrentDocument is IByteOffsetNavigable r) await r.JumpToByteOffsetAsync(offset);`, taking the
shell from one concrete-type match to zero. [architecture.md](architecture.md) documents that match
in two places — the `IDocumentViewModel` bullet and the failure-banner bullet — and both need
updating to the rule that then actually holds: *the shell reaches into a document only through
capability interfaces it opts into, never by concrete type*. This does not contradict that doc's
reasoning, which argues against putting `JumpToByteOffsetAsync` on `IDocumentViewModel` itself —
correct, since that interface is the surface every document genuinely shares. An opt-in capability
interface is a different thing, and the codebase already models capabilities this way elsewhere
(`JsonToolbarViewModel.SupportsPathNavigation`, capability-by-injected-delegate).

Pushing reveal into `DocumentViewCatalog`'s registration table instead — which would leave the
shell with zero type tests — was considered and rejected; see
[json-array-table-options.md](json-array-table-options.md) §3.

Mirror `SwitchViewAsync`'s existing progress/status-text pattern so there's no new UX language to
invent. `NavigateToPathAsync` racing a still-building index is already handled: `JsonPathResolver`
waits for coverage.

### 5. View

New `JsonArrayTableView.axaml`, structurally CSV's grid markup with the banner row added above it.
The copy is not markup-only: `CsvView.axaml.cs` is ~130 lines of code-behind (sticky-header
horizontal scroll mirroring, column scroll-into-view) that the table needs too. `CsvView.axaml`'s
compiled bindings are typed to `CsvViewModel` (`x:DataType`, plus
`$parent[csv:CsvView].((csv:CsvViewModel)DataContext)` casts), so copying is the cheaper path than
abstracting a shared base view; revisit only if a third table-like view shows up.

### 6. Shell registration

Two `DataTemplate`s in `MainWindow.axaml`'s `Window.DataTemplates`
([MainWindow.axaml:20-47](../Argonaut/Shell/MainWindow.axaml)): `JsonArrayTableViewModel` →
`JsonArrayTableView`, and `JsonArrayTableToolbarViewModel` → its toolbar view. Plus the
`ArrayTableService.Requested` subscription in `MainWindow.axaml.cs`, alongside the existing
`RawJumpService` one at line 51.

## Tests

Beyond the per-type unit tests, three existing suites are where this feature's real invariants
live and a new document that is absent from them is untested against exactly the bugs the base
classes exist to prevent:

- `CollectionDisposedEmptyTests` — `JsonArrayRowCollection` reports `Count == 0`, a null indexer
  and an empty enumerator after the document is disposed.
- `DocumentDisposalLifecycleTests` — the table document tears down cleanly with a scan in flight.
- `IndexedDocumentViewModelTests` — the base's contract, including that `MonitorIndexing` was
  started by `LoadAsync` before it returned.

Plus a `JsonArrayTableSessionTests` in the shape of `JsonDiffSessionTests`: `IndexingTask` is the
element index's, `Dispose` is idempotent, `TearingDown` fires from either direction.

Not in scope here: editing cells, sorting/filtering the table, searching within it (see
`CreateSearchNavigator` above), or exporting it back out (that last one is closer to the "export
subtree to file" idea from the earlier features list — worth keeping in the same neighborhood but
a separate feature).
