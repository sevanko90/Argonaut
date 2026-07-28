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
  a document with nothing searchable), `CanHandleFileType(FileKind)`, and observable
  `IndexFailure`. Implemented by `JsonViewModel`, `NdJsonViewModel`, `CsvViewModel`,
  `RawViewModel`, and the placeholder `IncompatibleViewModel`.
- Each document view model owns its whole status line (initial, live indexing %, complete,
  failed, and — NDJSON — selected-line), which the shell mirrors into the status bar.
- Deliberately NOT on the interface (kept until the planned per-view injectable toolbar):
  `HintSettings` and `SetDefaultExpandDepth`. The shell reaches these by concrete-type match.

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
  `MainWindowViewModel.JumpToFailureLocationAsync(byteOffset)`: switches to the raw viewer (if
  not already showing it) via `SwitchViewAsync`, then concrete-type-matches `CurrentDocument` to
  `RawViewModel` (same precedent as HintSettings/SetDefaultExpandDepth above) and calls
  `RawViewModel.JumpToByteOffsetAsync`, which resolves the offset to a display row via the
  existing `RawOffsetRowResolver` (waiting out an in-progress scan if needed - the same machinery
  `RawSearchNavigator` uses for a search reveal) and selects it. A resolve that outlives the
  document (closed/switched away mid-wait) surfaces as a catchable `ObjectDisposedException`
  from the now-unmapped file, not a crash - `JumpToByteOffsetAsync` swallows it, since there is
  nothing left to reveal.

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
  once `Start` is called (disposes it even if the index factory throws).

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
  - stale open losers (a newer open bumped `openRequestId` mid-load) and failed loads —
    before they ever become `CurrentDocument`;
  - the outgoing `CurrentDocument`, **before** the swap, in `SetCurrentDocument`.
- Disposing before the swap is critical: once disposed, the document's collections report
  empty, so Avalonia's trailing walk of the outgoing ItemsSource is a no-op — instant, and
  touching no unmapped memory — regardless of Avalonia's detach/enumerate ordering.
- The hosting view's `DetachedFromVisualTree` also disposes its `DataContext`, as an
  idempotent safety net for teardown the shell doesn't drive (e.g. window close).
- `Dispose` is idempotent on every document VM and on `IndexedFileSession` / the collections,
  so the two owners touching the same instance is harmless.
- Nested per-line `JsonViewModel` (inside NDJSON) is owned by `NdJsonViewModel`: disposed on
  each new line selection (`LoadSelectedLine` disposes the previous) and in its `Dispose`.

## Search interaction

- `FindController` owns one `FileSearchSession` at a time; its background scan holds spans over
  the current `MMapFile`. It MUST be stopped before that mapping is disposed — callers
  `await FindController.DetachAsync()` before any content swap / document disposal.

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
