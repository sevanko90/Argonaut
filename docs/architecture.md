# Argonaut architecture — shell, views, view models, mmap & disposal

Reference for how a loaded file flows from disk to screen, and — the part that has bitten us
repeatedly — who owns and releases the memory mapping. Keep this in sync when the ownership
chain changes.

## Shell

- `MainWindow` (`Shell/MainWindow.axaml[.cs]`) is a thin view: window input (find shortcuts,
  drag/drop), file picker, replace-confirmation dialog, toast, and the theme-mode reaction
  (variant + toggle icon). No file-open/close or status logic.
- `MainWindowViewModel` owns all shell state and the open/close lifecycle: `CurrentDocument`,
  status line, title, toolbar visibility, recent files, find controller, and the theme /
  expand-depth / date-hint preferences.
- `MainWindow.axaml` binds `ContentControl.Content="{Binding CurrentDocument}"`; implicit
  `DataTemplate`s map each document view model to its view (`JsonViewModel`→`JsonView`, etc.).
  `EmptyStateView` shows when `!IsFileOpen`.
- File loading is injectable via `MainWindowViewModel.DocumentLoader` (tests supply fakes).

## Documents

- `IDocumentViewModel` (`Shell/IDocumentViewModel.cs`) is the shell's slim view of one open
  document: `FilePath`, observable `StatusText`, `CreateSearchNavigator()`. Implemented by
  `JsonViewModel`, `NdJsonViewModel`, `CsvViewModel`.
- Each document view model owns its whole status line (initial, live indexing %, complete,
  failed, and — NDJSON — selected-line), which the shell mirrors into the status bar.
- Deliberately NOT on the interface (kept until the planned per-view injectable toolbar):
  `HintSettings` and `SetDefaultExpandDepth`. The shell reaches these by concrete-type match.

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
