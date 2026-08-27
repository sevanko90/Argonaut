# View a JSON array as a table — implementation plan

Let user pick a `[ { ... }, { ... } ]` node in the JSON tree and view it as a CSV-style grid,
reusing the CSV viewer's rendering, with a banner naming where it came from and a link back.

**Approach.** Picking "View as table" swaps `CurrentDocument` for a `JsonArrayTableViewModel` — a
real `IDocumentViewModel`, published the way a diff is — which opens its own independent session
over the array's byte range. Its toolbar carries the provenance banner, the column-mode dropdown,
and a Back command that reloads the origin file as JSON and navigates to the path it came from.

The options weighed on the way here (inline in the tree, a split pane, a second OS process; shared
vs. independent sessions; how the shell reveals a location; how widths are measured) are recorded
in [json-array-table-options.md](json-array-table-options.md). This document is the work.

## What already exists (and lowers cost)

1. **Programmatic navigation already ships.** `JsonViewModel.NavigateToPathAsync(string path)`
   ([JsonViewModel.cs:198](../Argonaut/Features/Json/JsonViewModel.cs)) resolves a JSONPath via
   `JsonPathResolver` and calls `SelectToken`, which expands ancestors and scrolls the tree to
   the token (`JsonVisibleRowCollection.EnsureVisible`). The "drive the JSON view back to where
   you came from" requirement in the ask is not new work — it is calling this with the path the
   table was opened from.
2. **Sub-range mapping and sub-range indexing already ship.** `MMapFile(string path, long offset,
   long length)` ([MMapFile.cs:46](../Argonaut/Infrastructure/MMapFile.cs)) maps just a byte range
   as its own independent OS-level mapping, decoupled from any other mapping over the same path;
   `JsonViewModel.LoadAsync(path, offset, length)`
   ([JsonViewModel.cs:291](../Argonaut/Features/Json/JsonViewModel.cs)) already builds a whole
   `IndexedFileSession<JsonStructureIndex>` over one, for the per-line NDJSON sub-documents.
   **This is the primitive the table view is built on**: the array's `[`…`]` byte range is a
   valid JSON document on its own, so the table view can own an independent session over exactly
   that range and never share state with the JSON view it came from. See stage 1.
3. **The shell already publishes documents that the catalog never built.**
   `MainWindowViewModel.OpenDiffAsync` ([MainWindowViewModel.cs:235](../Argonaut/Shell/MainWindowViewModel.cs))
   constructs a `JsonDiffViewModel` directly, loads it, and publishes it via `PublishDocument`
   with `FileTypeDetector.FileKind.Unknown`. It is the exact template for the table's entry point,
   *including* the pre-swap discipline the feature must not skip (see stage 3). Note also that
   `SetCurrentDocument` ([MainWindowViewModel.cs:589](../Argonaut/Shell/MainWindowViewModel.cs))
   is **private** — the entry point is a new public method on `MainWindowViewModel`, not a call
   into `SetCurrentDocument` from elsewhere.
4. **A view can already ask the shell to do something without referencing it.** `RawJumpService`
   ([RawJumpService.cs](../Argonaut/Infrastructure/RawJumpService.cs)) is a static event that
   `JsonView`'s "view in raw" link raises ([JsonView.axaml.cs:315](../Argonaut/Features/Json/JsonView.axaml.cs))
   and `MainWindow` alone subscribes to. Per [architecture.md](architecture.md), `JsonView` never
   holds a reference back to `MainWindowViewModel`; the "View as table" action uses the same shape.
5. **The CSV grid is not reusable data-layer-first.** `CsvRowCollection` reads raw CSV-syntax
   bytes straight off `MMapFile` via `CsvFieldReader` — it has no concept of a JSON token. What's
   reusable is the *presentation* layer: `CsvView.axaml`, `CsvCell`, `CsvColumnLayout`
   (`CsvColumnLayout.cs`), and the `CsvVisibleRow` shape. A JSON-array table needs a new
   `IList`-adapter (call it `JsonArrayRowCollection`) that walks `JsonStructureIndex` tokens the
   way `JsonRowFactory`/`DescribeChildCount` already do, and produces the same `CsvCell` shapes —
   not a rewire of `CsvRowCollection` itself. See "Extracting `CsvStructure`" below for the one
   piece of the CSV layer that *is* worth generalising first.
6. **Column discovery is a bounded token walk, not a DOM.** For each array element
   (`StartObject` token), walk its direct children only (skip nested containers via
   `EndIndex`, exactly like `JsonRowFactory.DescribeChildCount`
   ([JsonRowFactory.cs:142](../Argonaut/Features/Json/JsonRowFactory.cs))) to collect property
   names as columns, and decode each scalar via `JsonRowFactory.BuildScalarText`
   ([JsonRowFactory.cs:164](../Argonaut/Features/Json/JsonRowFactory.cs)). Both are *instance*
   methods on an `internal sealed` class constructed with `(index, mmap, hintProviders)` — the
   table view builds its own `JsonRowFactory` over its own session. Same O(children) cost model
   the tree view already pays per row — no new scanning primitive needed.
7. **Ownership friction.** `IDocumentViewModel.Dispose` today assumes it solely owns its
   `IndexedFileSession` (see the interface's lifetime contract in
   [IDocumentViewModel.cs:9-29](../Argonaut/Shell/IDocumentViewModel.cs)), and
   `MainWindowViewModel.SetCurrentDocument` disposes the outgoing document *before* the swap.
   That contract is why the table view opens its own session rather than borrowing one, and why it
   pays a reindex on each hop (the alternative, and the conditions for revisiting it, are in
   [json-array-table-options.md](json-array-table-options.md) §2). **The table view must not hold
   the origin document's `JsonStructureIndex` or `MMapFile`** — both are disposed before the table
   is published, and reading them afterwards is a native use-after-free, not a catchable
   exception.

## Commit order

Two of the pieces below are refactors of working code that the table feature needs but does not
own. They land first, separately, so the feature diff is the feature and each refactor can be
reviewed (and reverted) on its own terms.

1. **Capability interfaces.** Add `IPathNavigable` / `IByteOffsetNavigable` (stage 4), declare them
   on `JsonViewModel` / `RawViewModel`, convert `JumpToRawOffsetAsync` to query the capability
   instead of matching `RawViewModel`, and update [architecture.md](architecture.md)'s
   "the shell's only such match" note to the rule that then holds. Two lines of shell change plus
   two interface declarations; no behaviour change, existing tests cover it. Doing this first means
   the table's Back consumes a mechanism that already exists rather than introducing one.
2. **`CsvStructure` extraction.** Replace `CsvColumnLayout` with `CsvStructure` (names + widths +
   prebuilt `HeaderCells`), move measurement from decoded `string[]` rows to spans, and add
   `SetStructure` to `CsvRowCollection`. Touches `CsvColumnLayout.cs`, `CsvRowCollection.cs`,
   `CsvViewModel.cs`, `CsvView.axaml.cs`, `CsvColumnLayoutTests.cs`, `CsvRowCollectionTests.cs`.
   Standalone value regardless of this feature: it removes a per-open decode of up to 250 rows
   from the CSV/TSV load path.
3. **`JsonArrayRowCollection` + `JsonArrayTableViewModel`** (stages 1-2). Pure new code in
   `Features/Json`, testable without the shell - a `CsvStructure` and a sub-range session are
   both constructible in a test.
4. **Entry point + Back** (stages 3-4). The `ArrayTableService` event, `MainWindow`'s
   subscription, and `MainWindowViewModel.OpenArrayTableAsync`.
5. **View + shell templates** (stages 5-6). `JsonArrayTableView.axaml` + code-behind, the toolbar
   view, and the two `DataTemplate` registrations.

Steps 1 and 2 are independent of each other and of the decision to build this feature at all;
steps 3-5 are the feature.

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
  `UpdateHeaderCells` ([CsvViewModel.cs:202](../Argonaut/Features/Csv/CsvViewModel.cs)), which
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

    /// CSV/TSV: names from the header line (or "Column N"), widths from sampled rows.
    public static CsvStructure FromSample(IReadOnlyList<string> names, IReadOnlyList<string[]> sampleRows);
    /// JSON by-property: names are discovered property names, widths from sampled decoded values.
    /// JSON reshape: names are "Column 1".."Column N".
}
```

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
- `JsonTokenInfo.Length` is already that number - the token's content byte length, unpacked O(1)
  from the packed token log with no read of the mapping at all. (Byte length over-counts
  multi-byte UTF-8 slightly; irrelevant against a heuristic that clamps.)
- The formula saturates: `Math.Clamp(maxChars * 7.0 + 16.0, 60.0, 320.0)` gives the max width at
  or past ~44 characters and the min at or below ~6, so the over-count and the sample size both
  stop mattering quickly.

So re-widthing for a new N is a walk over the sampled elements' `token.Length` values, bucketed by
`i % N`. No file read, no strings, no retained side array. **Do not cache decoded sample text** -
`CsvViewModel` deliberately doesn't (its `sampleRows` is a local, freed once the layout is built),
and on pathological data - a minified document forced into a line-oriented view, one array element
holding a megabyte - retention is exactly where it hurts.

**Sample size**: reuse the initial-paint batch the load already awaits, as `CsvViewModel` does
with `InitialIndexedRowTarget` (250) - those elements are already indexed by the time the first
frame needs them, so this is not a separate read. This is only correct once measuring is free (see
directly below); a wide sample on top of a per-row decode is the one combination to avoid. A
narrow two-row sample and a cached-text alternative were both considered and rejected - see
[json-array-table-options.md](json-array-table-options.md) §4.

**Measure from spans, not strings** (do this as part of the refactor - it is what justifies the
sample size above): `CsvColumnLayout.Compute` currently takes decoded `string[]` rows,
which is why `CsvViewModel.LoadAsync` decodes up to 250 rows (bounded by `MaxDisplayFields` = 1000
fields x `DisplayText.MaxLength` = 1024 chars each) purely to measure them. `CsvFieldReader.SplitToSpans`
already returns `CsvFieldSpan(Offset, Length)`, so a span-based `Compute` would delete that decode
outright - and `Compute`'s signature is being changed by this refactor regardless. The JSON table
never had the decode to begin with: `JsonTokenInfo.Length` is the measurement.

**Blast radius**: `CsvColumnLayout.cs`, `CsvRowCollection.cs`, `CsvViewModel.cs`,
`CsvView.axaml.cs` (`ScrollColumnIntoView` takes the layout), plus `CsvColumnLayoutTests.cs` and
`CsvRowCollectionTests.cs`. Small and mechanical; `CsvView.axaml` itself binds only `HeaderCells`
and `Rows`, so the markup is unaffected. Do this first, as its own commit, so the JSON table
consumes a shape that already exists rather than growing a parallel one.

## Rough stages

1. **`JsonArrayRowCollection`** — constructed over the table view's **own** session (see stage 2),
   *never* over the origin document's index or mapping. Given `(JsonStructureIndex, MMapFile,
   JsonRowFactory, CsvStructure, mode)`, lazily produce `CsvVisibleRow`s: one pass over the root
   array's direct children to size `Count`, mirroring `CsvRowCollection.GetRow`'s
   on-demand-plus-LRU-cache shape for realization. Non-object elements (scalars, nested arrays)
   get a single "value" column — don't special-case ragged arrays beyond that. Column discovery
   itself (union or first-N-sample of property names, order, dedupe) happens in stage 2 and
   arrives as a finished `CsvStructure`; this collection never invents columns.

   **Column mode: by-property vs. flattened N-way reshape.** Some flat arrays are really
   n-dimensional data flattened into one sequence (interleaved coordinates, RGB triples, a
   fixed-width record repeated with no object wrapper) — `[x0, y0, x1, y1, ...]` rather than
   `[{x, y}, {x, y}, ...]`. Give the table a second column mode alongside "by property": **reshape
   into N columns**, N chosen 1-5 from a toolbar dropdown. Element `i` of the flat array goes to
   `Rows[i / N]`, `Column[i % N]` — row-major, same walk order as the array itself, so it's a pure
   re-chunking of `JsonArrayRowCollection`'s existing per-element decode, no new value-reading
   path. `Count` becomes `ceil(arrayLength / N)`; header cells are the generic `Column 1..N` names
   carried by the `CsvStructure` (the same labels `CsvViewModel.UpdateHeaderCells` synthesizes for
   a headerless CSV today).

   A partial last row simply yields fewer `CsvCell`s than the header has columns, and the row's
   `ItemsControl` renders that many cells — each cell carries its own width, so nothing
   misaligns; no padding and no special case are required. (This is *not* the same thing as
   `CsvRowCollection.GetRow`'s empty-row fallback, which covers an out-of-range **row** index.)

   This mode has no correct/incorrect N to validate against — the whole point is the user is
   telling the view something about the data's shape that the JSON itself doesn't encode. Picking
   a wrong N is not an error case: it just produces a table with values that don't line up
   column-to-column across rows. No warning, no rejection — surfacing that mismatch is on the
   user, since there's no signal in the data to distinguish "wrong N" from "ragged input" from
   "intentional." Available for any array regardless of element shape — an array of objects can be
   reshaped too (each cell shows that element's container summary), it's just less likely to be
   what someone wants there.

2. **`JsonArrayTableViewModel`** — implements `IDocumentViewModel`. Constructed with
   `(string filePath, long arrayOffset, long arrayLength, string originPath)`; its `LoadAsync`
   starts `IndexedFileSession<JsonStructureIndex>.Start(new MMapFile(filePath, arrayOffset,
   arrayLength), JsonStructureIndex.StartIndexing, reporter)`, awaits a small initial token batch
   the way `JsonViewModel.LoadCore` does, samples the first N elements to build the initial
   `CsvStructure` (widths measured from each sampled element's `JsonTokenInfo.Length` - no
   decode, nothing retained; see the `CsvStructure` section), and creates the row collection. Interface members to fill in, none of which the
   earlier draft assigned:

   - `FilePath` — the origin file's path (the banner shows the range, not a synthetic name).
   - `IndexingTask` — the session's; the shell awaits it to know when to stop overwriting the
     status line with scan progress.
   - `IndexFailure` + `StatusText` — same `MonitorIndexingAsync` shape as `JsonViewModel`/
     `CsvViewModel`. A malformed range surfaces as a normal partial/failed index, and a failure
     with `ItemsIndexed == 0` lands in the incompatible placeholder via the shell's existing path.
   - `CreateSearchNavigator()` — **return null for v1** (find is disabled while in table view).
     A `CsvSearchNavigator` analogue over token offsets is a separate piece of work; returning
     null is the contract's supported answer and the shell already handles it (`IsFindAvailable`).
   - `WindowTitle` — null (shell default) is fine; the banner carries the provenance.
   - `CanHandleFileType` returns false everywhere — it's never reached via `DocumentViewCatalog`,
     only constructed directly, exactly like `JsonDiffViewModel`.
   - `Toolbar` — a `JsonArrayTableToolbarViewModel` exposing the origin path/file text, the
     column-mode dropdown (by-property / reshape 1-5), and `BackAsync`. Changing the mode builds a
     new `CsvStructure` and calls `SetStructure` on the row collection (cache clear + Reset), it
     does not re-walk the array.

3. **Entry point** — a row-level action on array nodes in `JsonView`/`JsonRowPresenter` ("View as
   table"), raised through a static `ArrayTableService`-style event in the shape of
   `RawJumpService`, with `MainWindow` as sole subscriber; `JsonView` gets no reference to the
   shell. The JSON view resolves, *before* raising: the array's `startToken`, its
   `endToken = index.GetToken(startToken.EndIndex)`, the byte range, and
   `JsonPathBuilder.Build(...)` for the origin path.

   **Offsets are mapping-relative, not file-relative.** `JsonStructureIndex` records each token's
   offset relative to the `MMapFile` it indexed. That equals the file offset for a normally-opened
   JSON document (mapped from 0), but *not* for the per-line `JsonViewModel` instances NDJSON
   nests, which are already sub-range mappings. Either restrict "View as table" to the shell-level
   JSON document for v1, or carry the origin mapping's base offset alongside the token range and
   add it before constructing the table's `MMapFile`.

   **Indexing race**: `EndIndex` is `-1` until the container closes, so on a still-indexing file
   the action must wait for it (the `WaitForEndIndexAsync` pattern at
   [JsonPathResolver.cs:132](../Argonaut/Features/Json/JsonPathResolver.cs)) or be disabled until
   then. "Enabled for any array with at least one element" is not a sufficient guard.

   The shell handler is a new **public** `MainWindowViewModel.OpenArrayTableAsync(string path,
   long offset, long length, string originPath)`, modelled line-for-line on `OpenDiffAsync`
   ([MainWindowViewModel.cs:235](../Argonaut/Shell/MainWindowViewModel.cs)) — none of these steps
   are optional:

   - `var requestId = ++openRequestId;`
   - `await DetachFindAsync();` **before** the swap — a live find scan holds spans over the
     outgoing `MMapFile`, and disposing under it is a native use-after-free.
   - `FindBarResetRequested?.Invoke();` and `indexProgressReporter?.Stop();`
   - load, catching and logging via `OpenDebugLog`; dispose the document if a newer request won
     the race (`requestId != openRequestId`).
   - `PublishDocument(document, path, FileTypeDetector.FileKind.Unknown, addToRecents: false);`

   **`FileKind.Unknown` is deliberate**, same as diff: the view switcher shows no selection for
   the table, and picking any view there re-indexes the origin file as that kind through the
   normal switch path — a free second route back. Publishing as `FileKind.Json` instead would make
   `SwitchViewAsync` no-op on `kind == currentKind` and strand the user on the banner link.

4. **Back** — reload the origin path as JSON via the existing switch path, then navigate to
   `originPath`. Neither `SwitchViewAsync` ([MainWindowViewModel.cs:427](../Argonaut/Shell/MainWindowViewModel.cs))
   nor `LoadAndPublishAsync` ([MainWindowViewModel.cs:451](../Argonaut/Shell/MainWindowViewModel.cs))
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
   ([JsonViewModel.cs:198](../Argonaut/Features/Json/JsonViewModel.cs),
   [RawViewModel.cs:106](../Argonaut/Features/Raw/RawViewModel.cs)), so this is a declaration on
   each class and no new method bodies. Back becomes:

   ```csharp
   await SwitchViewAsync(FileTypeDetector.FileKind.Json);
   if (CurrentDocument is IPathNavigable nav)
       await nav.NavigateToPathAsync(originPath);
   ```

   This is a capability query, not a type switch: there is nothing here that grows a
   `case JsonViewModel:` arm as views are added, and a new document supporting path reveal
   implements the interface with no shell edit.

   **It also retires the existing match rather than adding a second.** `JumpToRawOffsetAsync`
   ([MainWindowViewModel.cs:186](../Argonaut/Shell/MainWindowViewModel.cs)) becomes
   `if (CurrentDocument is IByteOffsetNavigable r) await r.JumpToByteOffsetAsync(offset);`, taking
   the shell from one concrete-type match to zero. [architecture.md](architecture.md) currently
   documents that match as "the shell's only such match" — update it to state the rule that then
   actually holds: *the shell reaches into a document only through capability interfaces it opts
   into, never by concrete type*. Note this does not contradict that doc's reasoning, which argues
   against putting `JumpToByteOffsetAsync` on `IDocumentViewModel` itself - correct, since that
   interface is the surface every document genuinely shares. An opt-in capability interface is a
   different thing, and the codebase already models capabilities this way elsewhere (see
   `JsonToolbarViewModel.SupportsPathNavigation`, which is capability-by-injected-delegate).

   Pushing reveal into `DocumentViewCatalog`'s registration table instead - which would leave the
   shell with zero type tests - was considered and rejected; see
   [json-array-table-options.md](json-array-table-options.md) §3.

   Mirror `SwitchViewAsync`'s existing progress/status-text pattern so there's no new UX language
   to invent. `NavigateToPathAsync` racing a still-building index is already handled:
   `JsonPathResolver` waits for coverage.

5. **View** — new `JsonArrayTableView.axaml`, structurally CSV's grid markup with the banner row
   added above it. Note the copy is not markup-only: `CsvView.axaml.cs` is ~130 lines of
   code-behind (sticky-header horizontal scroll mirroring, column scroll-into-view) that the table
   needs too. `CsvView.axaml`'s compiled bindings are typed to `CsvViewModel`
   (`x:DataType`, plus `$parent[csv:CsvView].((csv:CsvViewModel)DataContext)` casts), so copying
   is the cheaper path than abstracting a shared base view; revisit only if a third table-like
   view shows up.

6. **Shell registration** — two `DataTemplate`s in `MainWindow.axaml`'s `Window.DataTemplates`
   ([MainWindow.axaml:19-47](../Argonaut/Shell/MainWindow.axaml)): `JsonArrayTableViewModel` →
   `JsonArrayTableView`, and `JsonArrayTableToolbarViewModel` → its toolbar view. Plus the
   `ArrayTableService.Requested` subscription in `MainWindow.axaml.cs`, alongside the existing
   `RawJumpService` one.

Not in scope here: editing cells, sorting/filtering the table, searching within it (see
`CreateSearchNavigator` above), or exporting it back out (that last one is closer to the "export
subtree to file" idea from the earlier features list — worth keeping in the same neighborhood but
a separate feature).
