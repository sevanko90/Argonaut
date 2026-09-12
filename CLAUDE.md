# CLAUDE.md

## Concept: Be mindful of memory footprint and performance
This application is designed to handle multi-gb files fast and with a low memory footprint. 
Feature design should take this into account and consider algorithms and types that minimise .NET
allocations and GC pressure. Operations that require heavy processing or full file scans should be
done on the background to keep the UI responsive. 

## Memory-mapped files: always use explicit, OS-reported data length

When working with `MemoryMappedFile`/`MemoryMappedViewAccessor` (see `Argonaut/Infrastructure/MMapFile.cs`), never treat
`MemoryMappedViewAccessor.Capacity` as the file's data length. `Capacity` is rounded up to the platform's memory
allocation granularity (this rounding differs between Windows and macOS), so it can be larger than the actual file
size and expose trailing zero-padding bytes as if they were real content.

Always source the true length from the file itself (e.g. `new FileInfo(path).Length`) and use that explicit value
everywhere data bounds matter (indexing loops, readers, length reported to callers). Only use the accessor/view
capacity for the mechanics of the mapping itself, never as a stand-in for "how much real data is here."

This has caused a real bug before: `JsonStructureIndex.Build` read past the real end of file on Windows (using
`MMapFile.Length` which returned `_accessor.Capacity`), fed trailing `0x00` padding into `Utf8JsonReader`, and
threw `JsonReaderException: '0x00' is invalid after a single JSON value`. It did not repro on macOS because the
padding rounding happened to align differently there. Fixed by storing `Length` from `FileInfo(path).Length` in
`MMapFile`'s constructor instead of deriving it from the accessor.

## Reading bytes: `IByteSource`, and why a whole range needs asking for

Every consumer of a document's bytes is typed to `IByteSource`, never to `MMapFile`. The concrete
mapping is named only by the sites that construct one; readers, indexes and row collections take
the interface, which is what lets a clipboard array or a downloaded payload be substituted without
touching them.

The interface has three members, and the important one does not promise what a caller usually
wants. `GetContiguousSpan(offset, maxLength)` returns **up to** what was asked for, truncated at
an internal boundary, because a span is a pointer and a length - it can only describe one
contiguous run of memory, and a piece table's logical range may live in two buffers. So:

- **Scan loops advance by the length returned, not the length requested**, and treat an empty
  return as the termination signal (`FileOffsetIndex.ProduceOffsets`, `FileTypeDetector`'s three
  finders, `FileSearchSession.Scan`). A loop that assumes it got its whole chunk silently skips
  bytes over a split source.
- **A whole range comes from `ByteSourceReading.RequireContiguous`**, which returns exactly the
  range or throws - the contract the old `MMapFile.GetSpan` had, and over any single-buffer source
  the identical zero-copy span. `ByteAt` is the single-byte peek (one byte can never straddle),
  and `GetUtf8String` is the one decode-on-demand idiom.
- **There is deliberately no pooled-gather helper.** Only `RawPieceTable` can split a range, and
  the raw viewer gathers inline at the four places it needs to (`RawRowReader.ReadRow` is the
  pattern: try contiguous, else `ArrayPool` + `CopyTo`) because only it knows each range's display
  cap. Everything else reads whole ranges of unbounded size - a whole NDJSON line, the JSON parse
  window - where renting would cost more memory than the read saves. If editing ever reaches those
  views, `RequireContiguous`'s call sites are the worklist.
- **`Release()` is for the one owner of a source**, the document session, and only after its
  cancel → join → release ordering (see `IndexedFileSession`). It is a no-op for a source holding
  no OS resource, which is why `IByteSource` does not extend `IDisposable`. Sub-range readers and
  search own their own sources and release those; nobody releases a source handed to them.

Where the parser already holds the bytes, take them from it rather than re-reading the source by
absolute offset - `JsonStructureIndex` hashes `reader.ValueSpan`, and only falls back to the
source when `HasValueSequence` says the token straddles a parse window.

## UI-threading convention: rely on the dispatcher's SynchronizationContext

Avalonia installs a `SynchronizationContext` on the UI thread, so an `await` in a method that *started* on the
UI thread resumes on the UI thread. The app leans on that guarantee as its one convention:

- **UI-originated async flows never dispatch explicitly.** No `Dispatcher.UIThread.InvokeAsync` wrappers around
  control access after an await — the await already resumed on the UI thread. Corollary: `ConfigureAwait(false)`
  is banned in app code, since it silently breaks this guarantee.
- **The one exception is code physically executing on a background thread** (e.g. `IProgressReporter.Report`
  called from inside an indexing scan, or a `Task.Run` body). That code marshals with `Dispatcher.UIThread.Post`
  — fire-and-forget, never `InvokeAsync`, because no worker should ever block on (or await) the UI thread.

If a method can be entered from either kind of thread, split it or document which side it belongs to rather
than sprinkling `CheckAccess`.

## Selection-bound setters must defer their side effects (`UiDeferral.AfterCurrentInput`)

A two-way binding on `SelectedIndex`/`SelectedItem` pushes into the view model *inside* Avalonia's selection
commit for the click that made the choice: `SelectingItemsControl.UpdateSelection` holds an open batch update
while the setter runs, and only afterwards enumerates `SelectedItems` against the control's `ItemsSourceView`.
So a setter that closes the popup hosting the list, or replaces the collection bound to its `ItemsSource`,
destroys the list the still-open commit is about to index into — and the commit throws
`ArgumentOutOfRangeException` out of `SelectedItems.GetEnumerator` on the dispatcher's input path, which is
unhandled and kills the process.

This is not the threading rule above (nothing is off-thread); it is re-entrancy. Such setters decide what was
chosen synchronously, then hand the *acting on it* to `UiDeferral.AfterCurrentInput` so it runs a dispatcher
turn later. Real bug: picking "Open schema folder…" in the schema flyout closed the flyout inline and crashed
the app on the next commit (`JsonToolbarViewModel.SelectedSchemaIndex`,
`SchemaRootPickerViewModel.SelectedPick`). Tests drive the deferral through `UiDeferral.PostOverride` via
`DeferredUiScope`, which keeps view-model tests dispatcher-free.

## Naming: say what it means, not what it is

Names carry intent. A member named for its *type* or its *mechanism* forces every reader to go
find out what it is for; a member named for what it represents answers that at the call site.

- **Banned as a whole name**: `Token`, `Cancel`, `Handle`, `Data`, `Info`, `Item`, `Value`,
  `Manager`, `Helper`, `Process`, `Update`. Each names a category, not a role.
- **Cancellation tokens are named for the event that fires them**, in the present participle,
  the way `IHostApplicationLifetime` does it (`ApplicationStopping`, not `StoppingToken`). So
  `IDocumentSession.TearingDown`, not `IDocumentSession.Token` — `CreateLinkedTokenSource(
  session.TearingDown)` then reads as a sentence, and a reader who has never seen the type knows
  when it fires.
- **Methods say what stops, not that something is cancelled.** `FileSearchSession.RequestStop()`,
  not `Cancel()` — and it matches `IDocumentSession.RequestStop`, so the same verb means the same
  thing (cooperative, returns immediately, joins nothing) everywhere in the codebase.
- **Two things of the same type in one class must be distinguished by name.**
  `RawIndexSession`'s `mappingCts`/`indexCts`, never `mappingCts`/`cts`: the bare one always
  reads as "the" one, and the whole point is that there are two with different lifetimes.
- **Type-suffixed private fields are fine** where the type genuinely is the meaning and only one
  exists (`revealCts`, `diffCts`). The rule bites hardest on public and internal members, and on
  anything crossing a type boundary — that is where a reader has no surrounding context to
  recover the intent from.

Applies to new code and to any name being touched anyway. Not a licence for sweeping renames of
settled internals.
