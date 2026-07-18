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
