# CLAUDE.md

## Memory-mapped files: always use explicit, OS-reported data length

When working with `MemoryMappedFile`/`MemoryMappedViewAccessor` (see `Infrastructure/MMapFile.cs`), never treat
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
