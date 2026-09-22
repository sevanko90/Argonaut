# Saving an edited document — plan

The raw view's edit mode changes the document through `RawEditController`: a `RawPieceTable`
over (original mapping, append-only scratch), a `RawEditedRowIndex` describing only the lines
edits touched, and a `RawEditJournal` for undo. Nothing writes to disk, and `IsDirty` is the
only record that the document differs from the file. This is the plan for writing it back.

## What save has to answer, beyond the copy

- **The rebuild budget.** Past 524,288 line records `RawEditedRowIndex.NeedsRebuild` fires and
  edits in *new* places are refused (`RawEditOutcome.NoRoomForAnotherEditSite`). A save resets the
  piece table to one piece over the new file, which clears it - more cheaply than the in-memory
  rebuild (freeze the piece table, scan it, layer a fresh single-piece table over the frozen one),
  which blocks editing for the whole scan and adds a read layer per rebuild.
- **Re-wrapping** is refused while a piece table exists, because it needs a fresh scan of the
  edited bytes. After a save that scan is the ordinary re-index, so re-wrap comes back for free.
- **Search reads the file on disk** through the origin, not the piece table. The chosen answer is
  that search reflects the last save and the status gutter's "Edited — not saved" says so; a
  merge-iterator over piece-space is a separate project. Save is what brings the two back into
  agreement.
- **Undo history** belongs to the outgoing piece table. A save ends it; carrying it across is not
  planned.
- **A document with no path** (a paste, a download) has nowhere to be replaced and gets Save As -
  the existing `Path is not null` degradation rule, not a new one.

## Rejected — patch the file in place

Only expressible for same-length edits, and even then it destroys the file if the process dies
mid-write.

## Chosen — streaming rewrite, then atomic swap

Walk the pieces in order and copy each into a staged file, flush to stable storage, swap it in.
One sequential pass at mapping speed - seconds for a 1GB file, taking the raw scan's ~1024 MB/s as
the ceiling for a byte copy - followed by the background re-index the app already performs on open.
The copy is not where the difficulty lives; the swap is.

### The swap is a platform seam, not a `File.Move`

Where the staged file can live, how the swap is made atomic, and what survives it all differ by
platform, and the Mac App Store sandbox forbids the naive version outright. So the writer that
walks the pieces never touches the file system; it writes to a stream a replacement abstraction
hands it. Sketched shape (names provisional, held to the naming rules in CLAUDE.md):

```csharp
/// Stages new content for a file and swaps it in atomically, or not at all.
public interface IFileReplacer
{
    /// Creates somewhere to write the new content for <paramref name="destination"/>, on the
    /// same volume, where this process is allowed to write. The destination need not exist yet
    /// (Save As, export).
    StagedFile Stage(IByteOrigin destination);
}

public abstract class StagedFile : IDisposable
{
    /// Where the piece walk writes. Sequential, unbuffered by the caller.
    public abstract Stream Content { get; }

    /// Flushes to stable storage and swaps the staged content in for the destination.
    /// Either the destination is the new content afterwards, or it is untouched.
    public abstract void Commit();

    /// Disposing an uncommitted stage deletes the staged content.
    public abstract void Dispose();
}
```

It takes an `IByteOrigin` rather than a path because on the sandboxed build the access grant
travels with the origin, and because a pathless document must not reach it at all.

**What every implementation must get right:**

- **Same volume, or it is not atomic.** A cross-volume rename is a copy then a delete. Stage beside
  the destination, or in a directory the OS guarantees is on its volume - never
  `Path.GetTempPath()`.
- **Flush to stable storage before the swap.** `FileStream.Flush(flushToDisk: true)`. On macOS
  plain `fsync` does not flush the drive's cache; confirm the runtime issues `F_FULLFSYNC`, and
  issue it directly if not.
- **Preserve what the user would notice losing.** A rename installs a new file, so permissions,
  ownership, ACLs and extended attributes (Finder tags, quarantine) go unless copied. Copy the mode
  bits at minimum; the platform APIs below preserve the rest.
- **Replace a symlink's target, not the link.** Resolve before staging. Hard links are broken by any
  rename-based save; accepted, and worth one line in user-facing notes.
- **Check free space first.** A save needs the full file size free on the destination volume.
- **Clean up an abandoned stage.** Name it recognisably (`.orders.json.argonaut-save-<random>`) so
  a later save to the same folder can sweep it.

**Implementations:**

- **Windows (portable and MSIX).** Stage beside the destination, commit with `File.Replace`
  (Win32 `ReplaceFile`, preserves attributes, ACLs and alternate data streams), or
  `File.Move(overwrite: true)` when the destination does not exist yet. MSIX runs full trust, so
  no store-specific branch.
- **macOS and Linux, unsandboxed.** Stage beside the destination, copy the mode bits, commit with
  `rename(2)` (`File.Move(overwrite: true)` on Unix). On macOS prefer the sandboxed implementation
  even here: it preserves metadata `rename` does not, and one macOS path is cheaper than two.
- **macOS under App Sandbox.** A picker or drop grants access to the file, not its folder, so a
  sibling temp file is denied. Use what `NSDocument` does:
  `NSFileManager.URLForDirectory(NSItemReplacementDirectory, appropriateForURL: destination)` for
  a permitted staging directory on the right volume, then `replaceItemAtURL:withItemAtURL:`,
  inside `startAccessingSecurityScopedResource` and ideally an `NSFileCoordinator` write so sync
  clients see one change. Needs native interop, the
  `com.apple.security.files.user-selected.read-write` entitlement, and recent-file bookmarks
  created without the read-only option. Only needed for a Mac App Store build
  ([store-distribution-comparison.md](store-distribution-comparison.md)).

### The mapping has to be gone before the commit

Windows cannot replace a file while any mapping of it is open, yet the piece walk reads the
original through that mapping. So a save is, in this order:

1. Stage, and walk the pieces into `Content` on a background thread, reading the original mapping.
2. Stop and join everything holding a source over this origin: the document session (its usual
   cancel → join → release) and any running search, whose chunk sources `FindController`
   otherwise cancels and forgets without joining.
3. `Commit()`.
4. Re-open the origin and start the background re-index as on open, with the piece table reset to
   one piece over the new file and the dirty flag cleared.

If step 3 fails the destination is untouched by contract: re-open the original origin and restore
the piece table and journal as they were - its offsets are logical, and the bytes they point into
are unchanged. Unix does not need step 2 (a mapping pins the old inode across a rename), but the
sequence does not branch per platform: one sequence, correct on the strictest.

## Build order

1. `IFileReplacer` with the Windows and unsandboxed implementations; the seam exists from the start
   even though the sandboxed one waits for a store build.
2. The piece walk into a stage, on the background, with progress through `IProgressReporter` and
   cancellation.
3. The save sequence above wired to edit mode: Save, and Save As for pathless documents.

The same seam serves the other writes queued in [roadmap.md](roadmap.md) - export a subtree, a
diff saved as an RFC 6902 patch - each a stream of bytes into a staged file with nothing different
about the commit.
