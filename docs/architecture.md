# Argonaut architecture — shell, views, view models, mmap & disposal

Reference for how a loaded file flows from disk to screen, and — the part that has bitten us
repeatedly — who owns and releases the memory mapping. Keep this in sync when the ownership
chain changes.

## Shell

- `MainWindow` (`Shell/MainWindow.axaml[.cs]`) is a thin view: window input (find shortcuts,
  drag/drop), file picker, replace-confirmation dialog, toast, and the theme-mode reaction
  (variant + toggle icon). No file-open/close or status logic.
- `MainWindowViewModel` owns all shell state and the open/close lifecycle: `CurrentDocument`,
  status line, title, toolbar visibility, recent files, find controller, the view switcher
  (`AvailableViews`/`SelectedView`), the failure banner (`IsFailureBannerVisible`), and the
  theme / expand-depth / date-hint preferences.
- `MainWindow.axaml` binds `ContentControl.Content="{Binding CurrentDocument}"`; implicit
  `DataTemplate`s map each document view model to its view (`JsonViewModel`→`JsonView`, etc.),
  including `IncompatibleViewModel`→`IncompatibleView`. `EmptyStateView` shows when `!IsFileOpen`.
- File loading is injectable via `MainWindowViewModel.DocumentLoader` (tests supply fakes; the
  real default is `DocumentViewCatalog.LoadAsync`).

## Documents

- `IDocumentViewModel` (`Shell/IDocumentViewModel.cs`) is the shell's slim view of one open
  document: `FilePath`, observable `StatusText`, `CreateSearchNavigator()` (nullable — null for
  a document with nothing searchable), `CanHandleFileType(FileKind)`, observable
  `IndexFailure`, and `Toolbar`. Implemented by `JsonViewModel`, `NdJsonViewModel`,
  `CsvViewModel`, `RawViewModel`, and the placeholder `IncompatibleViewModel`.
- Each document view model owns its whole status line (initial, live indexing %, complete,
  failed, and — NDJSON — selected-line), which the shell mirrors into the status bar.
- **Per-view toolbars are injected, not type-switched.** `IDocumentViewModel.Toolbar` is
  `object?` — null for a document with no toolbar (CSV) — and `MainWindow.axaml` binds
  `DocumentToolbarArea.Content` straight to `CurrentDocument.Toolbar`, resolving it through
  `Window.DataTemplates` keyed on the concrete toolbar type (`JsonToolbarViewModel` →
  `JsonToolbarView`, `RawToolbarViewModel` → `RawToolbarView`). `object?` is the honest
  contract because the shell never calls a member on it; the toolbar region therefore swaps
  with the document itself, and adding a new document view means adding one toolbar view
  model plus one `DataTemplate`, with no shell logic to touch.
- Consequently the shell reaches into a document view model for **nothing** except
  `JumpToRawOffsetAsync`'s `RawViewModel` match (see below). Toolbar-driven state is passed
  *down* at construction instead: the owning document view model builds its
  `JsonToolbarViewModel` in `LoadAsync`, handing it that document's own `DateHintSettings`
  and `JsonSchemaSettings` instances plus a `SetDefaultExpandDepth` callback (and, JSON only,
  a `NavigateToPathAsync` callback — NDJSON omits it, which hides the path entry via
  `SupportsPathNavigation`). The toolbar mutates the settings objects it was given; the
  document observes them.
- That "hand down the settings object" shape is what lets one toolbar serve a view hosting
  another view: `NdJsonViewModel` keeps *master* `HintSettings`/`SchemaSettings`/
  `DefaultExpandDepth` for the whole file and fans them into the currently selected line's
  nested `JsonViewModel` — `SetDefaultExpandDepth` forwards to it directly, the parsed
  `JsonSchemaDocument` is pushed by reference (parsed once, not once per line), and the
  hint-settings pair is mirrored in both directions with an `IsUserSelected` guard so an
  inferred default flowing up doesn't ping-pong against the master flowing down. A future
  multi-pane view (e.g. a diff) follows the same pattern rather than adding shell knowledge.

## View catalog & the view switcher

- `DocumentViewCatalog` (`Shell/DocumentViewCatalog.cs`) is the single kind ↔ view model
  mapping in the app: a static `(Create, Load)` registration table per view model, with the
  `FileKind → registration` map *derived* once at startup by probing each registration's
  throwaway instance with `CanHandleFileType` — not restated by hand. `Options` (JSON, NDJSON,
  CSV, TSV, Raw text) drives the status-bar `ComboBox`; `LoadAsync` has the exact shape of
  `MainWindowViewModel.DocumentLoader`, so it's both the production default and the same seam
  tests fake.
- The switcher's `SelectedView` setter fires `MainWindowViewModel.SwitchViewAsync(kind)` when
  the user picks a different kind; it's naturally inert when code sets it to mirror the
  already-current kind (on open, or after a switch completes), because the shell always updates
  `currentKind` before reassigning `SelectedView`.
- `SwitchViewAsync` re-indexes the *same* file as a different kind: unlike `OpenPathAsync`, it
  skips the replace-confirmation and doesn't touch recent files, but otherwise shares the exact
  publish path (`LoadAndPublishAsync`) — including the staleness guard and the pre-flight/failure
  handling below.

## Index failures & the incompatible-file placeholder

- `IFileIndexer.Failure` (`Infrastructure/IndexFailure.cs`) is non-null when a background scan
  stopped because of an error, null on success *and* on cancellation. `AppendLogIndexBase.RunIndexing`
  is the one place that catches a scan's exception, records it (via the overridable
  `DescribeFailure`, which `JsonStructureIndex` enriches with line/column/byte-offset from a
  `JsonException`), and rethrows — so `IndexingTask` still faults exactly as before.
- Forcing an incompatible kind onto a file (via the switcher) is classified in two stages:
  1. **Pre-flight** — `FileTypeDetector.IsPlausibleFor(kind, path, out reason)` is a cheap header
     check (no indexing) that rejects an obvious mismatch (e.g. CSV content forced to JSON)
     instantly.
  2. **Zero-progress rule** — if indexing still fails, `Failure.ItemsIndexed == 0` means nothing
     ever rendered, so the shell treats it the same as a pre-flight rejection; `ItemsIndexed > 0`
     means some of the file *is* valid, so the shell publishes the document with a warning
     banner (`IsFailureBannerVisible`) instead of discarding it.
- Both rejection paths swap in `IncompatibleViewModel` (`Shell/IncompatibleViewModel.cs`) via
  `MainWindowViewModel.ShowIncompatible`, which keeps `currentFilePath` set (so `IsFileOpen`,
  the switcher, and the close button all keep working) but never calls `FindController.Attach`
  — the caller already detached find before attempting the load, and the placeholder's
  `CreateSearchNavigator()` returns null (mirrored by `IsFindAvailable` hiding the find bar).
  `IncompatibleViewModel.Dispose()` is a no-op: it has no backing `MMapFile`/session to release,
  so it needs no special handling in the disposal ownership chain below beyond the normal
  outgoing-document dispose.
- A *late* failure (the initial batch loaded clean, but a background scan later throws) is
  caught the same way, via `MainWindowViewModel.OnDocumentPropertyChanged` watching
  `IndexFailure`: zero items swaps to the placeholder, some items just raises the banner.
- `IsFailureBannerVisible`, `FailureLocationText`, and `CanJumpToFailureLocation` are all
  computed straight from `CurrentDocument?.IndexFailure` (no backing fields, no dismiss) - the
  banner has **no dismiss affordance**: once a document is showing partial results, the warning
  stays up for that document's whole lifetime, since it only goes away by fixing/switching away
  from the actual problem. `SetCurrentDocument` and `OnDocumentPropertyChanged` both call
  `NotifyFailurePropertiesChanged()` to raise change notification for the three whenever
  `CurrentDocument` (or its `IndexFailure`) changes.
- Where a failure carries a byte offset (`JsonStructureIndex`'s enriched `DescribeFailure` always
  sets one; a pre-flight rejection never does, since it never got as far as reading a token),
  its "Line N" location is a clickable link — in the banner (`MainWindow.axaml`'s
  `JumpToFailureLineButton`) and in `IncompatibleView`'s location panel alike — that calls
  `MainWindowViewModel.JumpToRawOffsetAsync(byteOffset)`: switches to the raw viewer (if
  not already showing it) via `SwitchViewAsync`, then concrete-type-matches `CurrentDocument` to
  `RawViewModel` — the shell's only such match, because "jump to a byte offset" is meaningful
  for exactly one view and so has no place on `IDocumentViewModel` — and calls
  `RawViewModel.JumpToByteOffsetAsync`, which resolves the offset to a display row via the
  existing `RawOffsetRowResolver` (waiting out an in-progress scan if needed - the same machinery
  `RawSearchNavigator` uses for a search reveal) and selects it. A resolve that outlives the
  document (closed/switched away mid-wait) surfaces as a catchable `ObjectDisposedException`
  from the now-unmapped file, not a crash - `JumpToByteOffsetAsync` swallows it, since there is
  nothing left to reveal.
- A JSON row whose value was display-truncated (see `MaxDisplayTextLength` above) carries the
  overflowing value's file offset (`JsonRow.TruncatedValueOffset`); its truncation hint renders
  as a "view in raw" link (`JsonView.axaml`) that calls `RawJumpService.Request(byteOffset)` -
  the same view-to-shell decoupling `ToastService` uses, so `JsonView` never needs a reference
  back to `MainWindowViewModel`. `MainWindow` is the sole subscriber and forwards straight into
  `JumpToRawOffsetAsync`.

## Views ↔ view models

- Views are dumb: `JsonView` / `NdJsonView` / `CsvView` render bindings and forward input.
  Selection/scroll sync lives in code-behind; all behavior is in the view model.
- `NdJsonViewModel` hosts a nested per-line `JsonViewModel` (`SelectedLineJsonViewModel`) for
  the right-hand JSON pane. That nested VM has its own single-line sub-range mapping.

## Memory-mapped files

- `MMapFile` (`Infrastructure/MMapFile.cs`) is a read-only zero-copy view. Two ctors: whole
  file, and `(path, offset, length)` for a sub-range (one NDJSON line). The VM that needs a
  sub-range takes path+offset+length and creates its own mapping — callers never hand a
  mapping to a VM to free.
- `Length` always comes from `FileInfo`, never the accessor capacity (see CLAUDE.md).
- `GetSpan` throws `ObjectDisposedException` if used after `Dispose` — a use-after-free is a
  catchable managed error, never a silent access violation.
- `IndexedFileSession<TIndex>` (`Infrastructure/IndexedFileSession.cs`) owns the trio
  {mapping, background index, CancellationTokenSource} and encodes teardown ordering:
  cancel → join indexing task → join dependent tasks → release mapping. It owns the `MMapFile`
  once `Start` is called (disposes it even if the index factory throws). `RegisterDependentTask`
  joins background readers it didn't itself start (date-hint inference, JSON path resolution)
  before releasing the mapping. `RawIndexSession` is the wrap-width-restartable variant, with
  two cancellation sources: `mappingCts` for the document's lifetime and `indexCts` (linked from
  it) for the index `RestartIndex` recycles; `JsonDiffSession` composes two
  `IndexedFileSession<JsonStructureIndex>`s. All three implement `IDocumentSession`, which
  `IndexedDocumentViewModel` (below) drives — the teardown pair (`TearingDown` + `RequestStop()`
  + `Dispose()`) plus the two members the status line is driven from, `IndexingTask` and
  `Failure`. `TearingDown` is named for the moment it fires, per CLAUDE.md's naming convention.
- **`IDocumentSession` is deliberately not an index.** Two implementations own an `IFileIndexer`
  and the diff owns a `JsonDiffIndex` that isn't one, so a base class reaching for
  `session.Index` needed a nullable indexer accessor plus virtual escape hatches on
  `IndexingTask` and `MonitorIndexing` to route around the odd one out. Everything it actually
  wanted from an index was a task to await and a failure to report, so those are the members —
  stated at the level all three can answer them. `IndexingTask` is read **live**, never cached:
  `RawIndexSession` swaps it on a wrap-width restart, which is exactly what lets the completion
  monitor recognise a retired scan. `JsonDiffSession.Failure` is always null on purpose — a diff
  failure belongs to the left or right file, and only `JsonDiffViewModel` knows the display
  names to attribute it with.

## Virtualized ItemsSources

- `MemoryMappedCollectionBase` (`Infrastructure/MemoryMappedCollectionBase.cs`) is the shared
  base for the three list ItemsSources: `JsonVisibleRowCollection`, `MemoryMappedFileLineCollection`,
  `CsvRowCollection`. It supplies the read-only `IList` + `INotifyCollectionChanged` surface
  Avalonia's `VirtualizingStackPanel` needs.
- Subclasses implement only `GetCount()`, `GetItem(int)`, `DisposeCore()`. The base owns the
  `disposed` flag: `Count` returns 0 and the indexer returns null once disposed, and it
  short-circuits *before* calling the subclass — so a subclass cannot forget the guard.
- Why the guard exists: on a content swap Avalonia walks the outgoing ItemsSource once. On a
  multi-GB file a live walk both stalls for seconds (materializing every row) and, if the
  mapping is already gone, reads freed memory. Reporting empty makes that walk a no-op.

## Disposal ownership chain (the load-bearing part)

- **The shell (`MainWindowViewModel`) owns document disposal.** It disposes:
  - stale open losers (a newer open bumped `openRequest`, a `RequestTicket` — see "Staleness
    primitive" below — mid-load) and failed loads — before they ever become `CurrentDocument`;
  - the outgoing `CurrentDocument`, **before** the swap, in `SetCurrentDocument`.
- Disposing before the swap is critical: once disposed, the document's collections report
  empty, so Avalonia's trailing walk of the outgoing ItemsSource is a no-op — instant, and
  touching no unmapped memory — regardless of Avalonia's detach/enumerate ordering.
- The hosting view's `DetachedFromVisualTree` also disposes its `DataContext`, as an
  idempotent safety net for teardown the shell doesn't drive (e.g. window close). "The shell
  always stops find first" is therefore an unsafe assumption — and nothing depends on it any
  more: search reads its own mappings (below), and the one part that does touch document state
  (the reveal) links `TearingDown`.
- `Dispose` is idempotent on every document VM and on `IndexedFileSession` / `RawIndexSession`
  / `JsonDiffSession` / the collections, so the two owners touching the same instance is
  harmless.
- Nested per-line `JsonViewModel` (inside NDJSON) is owned by `NdJsonViewModel`: disposed on
  each new line selection (`LoadSelectedLine` disposes the previous) and in its `DisposeCore`.
- `IndexedDocumentViewModel` (`Infrastructure/IndexedDocumentViewModel.cs`) is the base class
  behind `JsonViewModel`/`CsvViewModel`/`NdJsonViewModel`/`RawViewModel`/`JsonDiffViewModel`.
  Its `Dispose()` is the one place the ordering above is encoded for a document:
  `session.RequestStop()` → `rows.Dispose()` → subclass `DisposeCore()` → `session.Dispose()`.
  It also owns `FilePath`/`StatusText`/`IndexFailure` and the indexing-completion monitor
  (`MonitorIndexing()`, started from `LoadAsync` before it returns — the shell's own
  continuation on `IndexingTask`, in `StopProgressWhenIndexedAsync`, depends on that ordering);
  subclasses react to completion/failure via `OnIndexingCompleted()`/`OnIndexingFailed(failure)`,
  not by hand-rolling their own monitor loop. There are no longer any escape hatches:
  `IndexingTask` and `MonitorIndexing` are non-virtual, and `JsonDiffViewModel` — the type that
  used to need them — now expresses its difference through the hooks, pointing both at one
  method because a side failure completes the diff **normally** over an empty index and so has
  to be attributed on the success path too.
- **The session and the row collection are abstract members** (`Session`, `MappedRows`), not
  registered by the subclass calling an `Attach…` during load. They are the two things the base
  exists to sequence, and an imperative registration can be silently forgotten — a load path
  that built its rows but never announced them would leave a growth monitor reading a mapping
  the session had already released, with nothing failing to point at it. Abstract members make
  that a compiler error. They're properties rather than constructor arguments because both are
  created partway through `LoadAsync`, after an await; reading them live also means
  `RawViewModel`'s wrap-width restart just replaces its field.
- `OnIndexingCompleted()` takes no argument on purpose. It used to be handed the `IFileIndexer`,
  which one override out of four read — and that one had a typed count of its own. Every
  subclass reports from state it already has.

## Search interaction

- The vocabulary splits in two: **search** is bytes (`ScanTarget`, `ISearchMatcher`,
  `FileSearchSession` — no display knowledge), **find** is what the user steps through
  (`FindCursor`, `FindStatusText`, `FindController`). `ISearchNavigator` is the seam: targets
  down, order keys and reveals up.
- `FindController` owns one `FileSearchSession` per searched target. A navigator hands over
  `ScanTarget`s (path, plus offset/length for a sub-document), never mappings —
  `ISearchNavigator.ScanTarget`/`ScanTargets`. A scan target is what is searched *in*; the term
  being searched *for* reaches the engine as an `ISearchMatcher`, never through the navigator.
- `FindCursor` owns the ordered stop list and the position in it: incremental folding, sorting
  by the navigator's opaque `OrderKey`, dropping matches the viewer cannot show, collapsing
  equal keys to one stop per row, and re-finding the selection by KEY after each fold (a match
  found late can sort ahead of it, so an index would not survive). It knows nothing about files,
  scans or view models — matches arrive through `IMatchSource` — so its subtleties are tested
  directly against plain lists. `FindStatusText` composes the status line as a pure function.
  What remains in `FindController` is orchestration: scan lifetime, waiting for more results,
  the reveal, and the press queue.
- **Each scan opens its own mapping, one 4MB chunk at a time, and releases it before taking the
  next.** So a search's lifetime is fully independent of the document's: a document can be torn
  down while a scan over the same path runs, and stopping a scan is never a precondition for
  releasing anything. `FindController.StopSearch()`/`Detach()` are synchronous — they ask the
  scans to stop and return; nothing joins them.
- **Why per-chunk and not one whole-file mapping** (do not "simplify" this back): a second
  whole-file mapping would double-count every touched page in RSS — a full search of a 4.5GB
  file would report ~9GB — and its eventual multi-GB unmap would contend for the process-wide
  address-space lock with the document's own unmap on the UI thread at close. One chunk caps
  both.
- `ISearchNavigator.DocumentTearingDown` deliberately has **no default implementation**, unlike
  every other optional member on that interface. The others default to correct single-file
  behaviour; the only possible default here is a token that never fires, which is not a weaker
  right answer but precisely the bug the member prevents. A navigator with no session writes
  `=> default` explicitly — greppable; an omission would not be. Do not add a default.
- **The reveal is the one document-scoped part of find.** It runs on the UI thread, awaits index
  coverage, and touches the document's index and rows, so `FindController` links
  `ISearchNavigator.DocumentTearingDown` (the document session's `TearingDown`) and swallows the
  cancellation. `MMapFile.GetSpan`'s `ObjectDisposedException` remains the backstop.
- The shell still stops find before a content swap — it clears the highlight term and find-bar
  status. That is UI state, not safety.
- A range `ScanTarget` reports offsets relative to the range start, so a sub-document (one
  NDJSON line, whose index is zero-based at the line start) is searched in the coordinate system
  its own index speaks. Nothing creates a navigator over a nested view model today; the range
  exists so that it would be correct if something did.

## Staleness primitive

- `RequestTicket` (`Infrastructure/RequestTicket.cs`) formalizes the codebase's "monotonic
  counter + comparison" idiom used to detect a newer request superseding an in-flight one:
  `Begin()` issues a ticket, `IsCurrent(ticket)` reads it back, `Current` reads the active
  ticket without issuing one. Backs `MainWindowViewModel.openRequest`,
  `NdJsonViewModel.selectionRequest`, and `FindController.findRequest`. Sealed class, not a
  struct — `Begin()` mutates, so a struct copy would silently start its own counter.

## Threading convention (see CLAUDE.md)

- UI-originated async resumes on the UI thread (Avalonia's SynchronizationContext); no explicit
  dispatch after an await, and `ConfigureAwait(false)` is banned in app code.
- Only code physically on a background thread marshals back, via `Dispatcher.UIThread.Post`
  (fire-and-forget), never `InvokeAsync`.

## Known open item

- Closing a multi-GB file has a small lag: `MMapFile.Dispose` unmaps a fully-resident view
  (~43ms/480MB, so ~400ms at 4.5GB) synchronously on the UI thread. Not yet moved off-thread;
  doing so needs a synchronous "release visible items" phase before the swap plus a background
  unmap, and making the shell the sole disposal owner to avoid a race with the view's detach.
