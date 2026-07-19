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
