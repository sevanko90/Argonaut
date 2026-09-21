# Editing a file in place — options considered

Decision record for "let the user change the document, not just look at it". It was written to
settle *where* editing would live and *how* an edit is represented before any of it was
scheduled, because the naive answer (mutate bytes, re-index) is the one answer that a multi-GB
tool cannot afford.

**Status (2026-09-21):** step 1 of the [Outcome](#outcome) is built, step 2 is built apart from
IME composition (caret, selection, copy, a position readout, and an edit mode in which typing,
deletion, Enter, paste and undo/redo change the document), steps 3-4 are not started - so an
edited document cannot yet be saved. The sections "What step 1 actually became", "What step 2 has
become so far" and "What typing became" at the end are the current record; §1-§6 are the reasoning
as written.

Every cost claim in §1-§6 was checked against the code at the time of writing (branch `main`,
2026-09-09), with file/line references kept so a future reader can tell whether the reasoning
still holds or the code has moved out from under it. Some has since moved: the
`MMapFile.GetSpan` chokepoint those sections cite is now the `IByteSource` seam (whole ranges via
`ByteSourceReading.RequireContiguous`, which kept `GetSpan`'s exact-or-throw contract), and
`FileSearchSession` is `SearchSession`, reading through `IByteOrigin.OpenRange`, and
`RawSegmentIndex.NextRowBoundary` is now `RawRowBoundary.Next`. The argument is
unchanged by either rename; the line numbers are not current.

The constraint that drives every choice: the app never holds a document in memory. It holds a
mapping (`MMapFile`) and an index, and realizes only the rows on screen. Any editing design that
requires the document in memory, or requires re-deriving a whole index per keystroke, is
disqualified on arrival regardless of how clean it looks.

## Decisions at a glance

- **How an edit is represented** — a piece table over (original mapping, append-only scratch),
  with logical offsets (option D). Rejected: mutating the file (A), copy-on-open (B). Kept as a
  cheaper special case for the JSON view only: an offset-keyed replacement overlay (C).
- **Which view gets the editor** — the raw view first, and possibly only (option B). Rejected:
  editing in the JSON tree first (A). Deferred: both (C).
- **What an edit costs the index** — raw re-derives, JSON overlays. The raw index is a pure
  function of the bytes and can be invalidated and regrown; the JSON index is a record of parse
  results and cannot.
- **How a save works** — streaming rewrite to a temp file, then atomic rename, with the staging
  and swap behind a platform seam (`IFileReplacer`) because the sandboxed macOS build cannot
  create a temp file beside the original. Rejected: in-place patching.
- **What happens to search while dirty** — search reflects the last save, and the document is
  marked dirty. Deferred: a merge-iterator over piece-space.

## 1. How an edit is represented

### A — Mutate the file

Write the change into the file on disk and re-index.

Any edit that changes a length has to shift every subsequent byte of the file, which is a full
rewrite per keystroke. Even a same-length overwrite invalidates every content hash the diff
indexer computed (`JsonStructureIndex` lines 338, 410-411) and forces a re-index. Disqualified
on the first sentence — this is the option that only works because it is how a small-file editor
already behaves, and it is exactly what does not scale here.

### B — Copy the document into memory on first edit

Read the file into a mutable buffer, edit freely, write it back.

Honest and simple, and it is what most editors do. It also forfeits the entire premise of the
app: a 3GB file becomes a 3GB (or, decoded, larger) allocation, on top of what the JSON
structural index already costs at that size — 24 bytes per token, about 2.24 GiB at 100M tokens
([index-memory-analysis.md](index-memory-analysis.md)). Disqualified.

### C — Offset-keyed replacement overlay

A dictionary from a token's original byte offset to a span in a scratch buffer. Reads consult it
before falling back to the mapping.

The attraction is that it fits the existing code almost invisibly. Every byte read in the app
funnels through `MMapFile.GetSpan(long offset, int length)` (`Argonaut/Infrastructure/MMapFile.cs:64`)
or its decoding sibling `GetUtf8String` (line 86) — 36 call sites app-wide, 9 of which are
`FileTypeDetector` probing the file at open rather than a display path. Crucially, in the JSON
paths those calls pass a *token's* offset and length, taken from a `JsonTokenInfo`
(`Argonaut/Features/Json/JsonStructureIndex.cs:44-52`) that is frozen in original-file
coordinates. So a replacement keyed by original offset can be served at the chokepoint with the
callers unchanged, and the index never learns that anything moved.

What it cannot express: anything that changes the *number* of bytes in a way the walk must see,
which is to say insertion and deletion. It is a genuine option only for "replace this value with
that value", and only where the index is structural rather than positional.

**Kept, but narrowly** — see §3, where it is the whole of a JSON-view stage 1.

### D — Piece table over (original mapping, append-only scratch) (chosen)

The document becomes an ordered list of pieces, each `(buffer, offset, length)` where buffer is
either the original mapping or an append-only scratch buffer. An edit splits at most two pieces
and inserts one. Nothing is ever mutated or moved; the scratch buffer only grows.

- Memory is proportional to *edits*, not to file size. Human-scale editing means a scratch
  buffer measured in KB against a file measured in GB.
- A logical offset maps to a physical `(buffer, offset)` by binary search over the piece list.
  That is the one new cost every read pays, and it is a handful of comparisons against a list
  whose length is the number of edits.
- Insertion and deletion are the same primitive as replacement, so undo/redo is a command log
  over one operation rather than four.
- It is a well-understood structure with no novel parts. The risk is in what consumes it, not
  in the structure.

**Chosen.** It is the only option that keeps the memory story intact while supporting real
editing, and option C degrades out of it cleanly for the JSON case.

## 2. Which view gets the editor

### A — The JSON tree

Edit values and keys in place in the tree, keeping structure valid by construction.

This is the feature a user would ask for, and it is the expensive one, because the JSON index
records parse results. Rebuilding it is a full re-parse of the file. A profiling run on a 1GB
synthetic document (2026-07-24, harness not committed) measured JSON indexing at ~283-428 MB/s
against ~1024 MB/s for the raw scan, which is a SIMD newline walk with no token parsing. So the
index cannot
be re-derived per edit, and every edit that changes structure has to be expressed as an overlay
that the row walk merges in. §3 breaks down what that costs.

It also inherits every reader that is positional rather than structural: array element counts,
JSONPath indices, the container child-count cache
(`Argonaut/Features/Json/JsonRowFactory.cs`, `ChildCountCacheCapacity`), schema row binding.

**Not first.** The most valuable version of this is also the most expensive, and it is not
reachable without the byte layer that option B needs anyway.

### B — The raw view (chosen to go first)

The raw view is currently treated as a fallback: a flat, virtualized list of display rows that
makes no assumptions about content, substituting U+FFFD for invalid UTF-8 and Unicode Control
Pictures for C0 controls (`Argonaut/Features/Raw/RawRowReader.cs`). That stance is exactly
right for an editor of last resort, and the view is far cheaper to make editable than the tree
for one structural reason:

**`RawSegmentIndex` does not store rows. It derives them.** It stores one `RawRowAnchor`
(16 bytes) per `AnchorStride = 64` rows (`Argonaut/Features/Raw/RawSegmentIndex.cs:48`), and
`GetRowInfo` (line 87) recovers any row in between by rescanning forward from the bucket anchor
through `NextRowBoundary` (line 285) — a bounded rescan of at most `AnchorStride × (WrapWidth + 1)`
bytes. The index is ~2.3MB for a 1GB file and is a pure, deterministic function of the bytes.

That changes the whole problem. Where the JSON view must *overlay* because it cannot rebuild,
the raw view can simply *re-derive*. Point `NextRowBoundary` and `RawRowReader.ReadRow` at
piece-space instead of file-space and the view is editable, with no structural overlay at all:
no tombstones, no fragment indices, no merge-walk. There is no structure to merge.

**Chosen to go first.** It is also the capability a big-file tool most conspicuously lacks: a
4GB JSON document that fails to parse at byte 3.2 billion cannot be repaired by any
structure-aware editor, because there is no structure to bind an edit to. The raw view is the
one view that still works when the document is broken.

### C — Both, sharing the byte layer

The end state, if editing earns it. Worth naming now only to record the sequencing: the piece
table built for B is the thing A needs, so A becomes a smaller project after B ships than before.

There is also a route where A is never built. If a save from the raw view triggers the re-index
the app already performs on open, the JSON view gets edited content for free, at the cost of the
user editing in raw and switching back. That trade should be re-evaluated after B, not now.

**Deferred.**

## 3. What an edit costs each index

### The JSON index — overlays, in three ascending classes

1. **Replace a scalar or a member name.** Served entirely by option C at the `GetSpan`
   chokepoint. The index is untouched, because it still correctly describes the original file
   and nothing downstream ever learns the replacement's length except the display path that
   reads it. Small, and self-contained.
2. **Delete a member or element.** Not a byte edit but a walk edit: tombstone the token range
   `[token, token.EndIndex]`. The visible-row walk already skips subtrees by `EndIndex`, so the
   skip itself is nearly free. The cost is in everything that *counts*: array indices for
   JSONPath, container child counts (cached by token index and deliberately surviving rebuilds —
   would need invalidating), schema row binding.
3. **Insert or paste a subtree.** The only class that needs a second structure. New content has
   no tokens, so it is parsed into its own small `JsonStructureIndex` over the scratch buffer —
   cheap, because inserted text is human-scale — and anchored to `(parentToken, positionAmongChildren)`.
   The row walk then becomes a merge-walk over original children and spliced fragments. This is
   the expensive class, and it is confined to `JsonVisibleRowCollection`'s walk.

### The raw index — deltas and bounded re-flow

An edit at logical offset X:

- Anchors *before* X are untouched.
- Anchors *after* X are valid with a constant `(byteDelta, lineDelta)` applied. No rescan.
- `lineDelta` is zero unless the edit inserted or deleted a `\n`.
- Re-flow caused by a forced wrap break is **bounded by the line**, not by the file, because a
  forced break is measured from the line start and a line ends at `\n`.

So a keystroke costs a bounded local rescan plus a delta entry, not a rescan of the tail. The
pathological case is worth stating plainly rather than hiding: editing inside a single 100MB
line re-flows that line.

Publishing the re-derived rows needs no new machinery. `RawSegmentIndex` is already an append log
that grows during a background scan and publishes at anchor boundaries (`publishedRowCount`,
`WaitForRowCountAsync`), and the view already handles growth and a final refresh. Invalidation is
"truncate the log at the affected anchor and let it regrow" — the path the index already takes on
first open.

## 4. How a save works

### Rejected — patch the file in place

Only expressible for same-length edits, and even then it destroys the file if the process dies
mid-write. Not worth having as a fast path.

### Chosen — streaming rewrite, then atomic rename

Walk the pieces in order and copy each to a temp file beside the original, then fsync and
rename. For the JSON overlay case the equivalent walk is over the original file, copying
verbatim until an edited offset, emitting the replacement, skipping tombstoned ranges to
`EndIndex`, splicing fragments at their anchors.

Either way it is one sequential pass at mapping speed — on the order of seconds for a 1GB file,
taking the raw scan's measured ~1024 MB/s as the ceiling for a pure byte copy — followed by the
background re-index the app already performs on open. Save cost is O(file), which is what every
editor pays and is not where the difficulty of this feature lives.

### The replace step is a platform seam, not a `File.Move`

"Temp file beside the original, then rename" names the right outcome but not an implementation,
because the only portable piece of it is the copy. Where the temp file can live, how the swap is
made atomic, and what survives it all differ by platform, and one target - the Mac App Store -
forbids the naive version outright. So the writer that walks the pieces never touches the file
system directly. It writes to a stream that a replacement abstraction hands it, and that
abstraction owns everything either side of the copy.

Sketched shape (names provisional, held to the naming rules in CLAUDE.md):

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

It takes an `IByteOrigin` rather than a path string because on the sandboxed build the path is not
sufficient on its own - the access grant travels with the origin (below) - and because a document
with no path (a paste, a download) has nowhere to be replaced and gets Save As instead, which is
the existing `Path is not null` degradation rule rather than a new one.

**What every implementation must get right**, whichever platform it is on:

- **Same volume, or it is not atomic.** A rename across volumes is a copy followed by a delete, so
  a crash between them loses the file. The temp file goes beside the destination, or into a
  directory the OS guarantees is on the destination's volume - never `Path.GetTempPath()`.
- **Flush to stable storage before the swap.** `FileStream.Flush(flushToDisk: true)`. On macOS a
  plain `fsync` does not flush the drive's own cache; confirm the runtime issues `F_FULLFSYNC`
  there before relying on it, and issue it directly if not.
- **Preserve what the user would notice losing.** A rename installs a *new* file, so the
  original's permissions, ownership, ACLs and extended attributes (including macOS Finder tags
  and quarantine flags) are gone unless copied. Copy the mode bits onto the staged file at minimum;
  the platform APIs below preserve the rest.
- **Replace the target of a symlink, not the link.** Resolve the destination before staging, or a
  save turns a link into a regular file beside the real one. Hard links are broken by any
  rename-based save; that is accepted and worth one line in the user-facing notes.
- **Check free space first.** A save needs the full file size free on the destination volume.
  Failing that up front is a message; failing it at 90% is a wasted minute and a temp file to clean.
- **Clean up an abandoned stage.** A crash leaves a hidden temp file beside the user's document.
  Name it recognisably (`.orders.json.argonaut-save-<random>`) so it can be found and swept on
  the next save to the same folder.

**Implementations:**

- **Windows (portable and MSIX).** Stage beside the destination, commit with `File.Replace`
  (Win32 `ReplaceFile`), which preserves attributes, ACLs and alternate data streams, or
  `File.Move(overwrite: true)` when the destination does not exist yet. The MSIX build runs full
  trust, so this is the same code with no store-specific branch.
- **macOS and Linux, unsandboxed.** Stage beside the destination, copy the mode bits, commit with
  `rename(2)` (which is what `File.Move(overwrite: true)` does on Unix). On macOS, prefer the
  sandboxed implementation below even here: it preserves metadata `rename` does not, and one
  macOS path is cheaper to maintain than two.
- **macOS under App Sandbox (Mac App Store).** The case that forces the abstraction. A file the
  user opened through the picker or a drop grants access to *that file*, not to its folder, so
  creating a sibling temp file is denied. The sanctioned route is the one `NSDocument` uses:
  `NSFileManager.URLForDirectory(NSItemReplacementDirectory, appropriateForURL: destination)` for
  a staging directory the sandbox permits on the right volume, then
  `replaceItemAtURL:withItemAtURL:` to swap, inside `startAccessingSecurityScopedResource` and
  ideally an `NSFileCoordinator` write so sync clients (iCloud Drive, Dropbox) see one coherent
  change. This needs native interop (Objective-C runtime calls or a small native shim); no .NET API
  reaches it. It also needs the `com.apple.security.files.user-selected.read-write` entitlement,
  and any security-scoped bookmark that should be saveable later (recent files) must be created
  without the read-only option.

**The mapping has to be gone before the commit, and that orders the whole save.** On Windows a file
cannot be replaced while any mapping of it is open, and `MMapFile` holds one for its lifetime
(`Argonaut/Infrastructure/MMapFile.cs:30`). But the piece walk *reads* the original through that
same mapping to produce the staged content. So a save is necessarily:

1. Stage, and walk the pieces into `Content` on a background thread, reading the original mapping.
2. Stop and join everything that holds a source over this origin - the document session (its
   usual cancel → join → release), and any running search, whose chunk sources `FindController`
   otherwise cancels and forgets without joining. A search that is not joined here can still hold
   a mapping when the commit runs.
3. `Commit()`.
4. Re-open the origin and start the background re-index, exactly as on open, with the piece table
   reset to a single piece over the new file.

If step 3 fails, the destination is untouched by contract, so the recovery is to re-open the
original origin and restore the piece table and journal as they were - offsets in the piece table
are logical, and the original bytes they point into are the same bytes as before. Unix does not
need step 2 to succeed (a mapping survives the rename, pinning the old inode), but the ordering
should not branch per platform: one sequence, correct on the strictest.

The same seam serves every other write the roadmap has queued - Save As, export a subtree
([json-array-table-options.md](json-array-table-options.md)), a diff saved as an RFC 6902 patch -
each of which is a stream of bytes into a staged file with nothing different about the commit.
[store-distribution-comparison.md](store-distribution-comparison.md) has the wider sandbox picture
this is one part of.

## 5. Search and the other direct readers

The readers that bypass the index and read the file themselves are where the sprawl is, and they
are the reason this is a coordinate-system change rather than a feature:

- **`SearchSession` opens its own source over the origin, one chunk at a time**
  (`Argonaut/Features/Search/SearchSession.cs`, `target.Origin.OpenRange`). It scans the bytes
  the origin holds - the file on disk - and cannot see a piece table or an overlay.
- **`RawOffsetRowResolver`** maps a byte offset back to a row for reveal — needs piece-space.
  Small.
- **`JsonDiffIndex`** reads name spans from its own two mappings, and the content hashes it
  relies on are computed during indexing from raw spans, so edits invalidate them.
- **`JsonArrayRowCollection` / `JsonArrayElementIndex`** match column names off `NameOffset`,
  which survives an option-C replacement but not an insertion or deletion.
- **`CsvFieldReader`, `NdJsonLineReader`** read spans directly and would need the same treatment
  if editing ever reaches those views.

**Chosen for now:** search reflects the last save, and the document carries a dirty flag that
says so. It is honest, it is cheap, and it does not compromise the fastest path in the app.
**Deferred:** a merge-iterator over piece-space, which is a project in its own right.

## 6. The caret

Worth recording because it inverts the intuition about where the work is. The piece table is a
few hundred lines of well-understood code. Turning a virtualized, read-only row list
(`Argonaut/Features/Raw/RawView.axaml.cs`) into something with a caret, selection spanning
rows, IME support, clipboard, and keyboard navigation that behaves correctly when rows are
being derived on demand underneath it, is very likely the larger half of the feature.

Any estimate for this work that prices the data structure and not the caret is wrong.

## Outcome

Build the byte layer once, in the raw view.

1. **Piece table + raw view reads.** `NextRowBoundary` and `RawRowReader.ReadRow` over
   piece-space, anchor deltas and bounded re-flow, `RawOffsetRowResolver` in piece-space. No UI
   yet — testable headlessly against a scripted edit sequence, in the shape the existing index
   tests already use.
2. **Editing UI in the raw view.** Caret, selection, clipboard, undo/redo command log. The large
   half; size it separately and do not fold it into step 1.
3. **Save.** Streaming rewrite into an `IFileReplacer` stage, release every mapping of the
   original, commit, background re-index. Dirty flag, and search marked as reflecting the last
   save. Build the Windows and unsandboxed implementations first; the sandboxed macOS one is only
   needed for a Mac App Store build, but the seam must exist from the start.
4. **Re-evaluate the JSON view.** With 1-3 shipped, decide between the option-C overlay for
   scalar edits in the tree (small, self-contained, no index change) and leaving structural
   editing to the raw view. Do not commit to JSON edit classes 2 and 3 before this point.

Stopping after step 3 leaves a genuinely useful tool. That is the main argument for this
sequencing over the one that starts in the tree.

## What step 1 actually became

Step 1 of the outcome above is built. Three notes where the implementation departed from, or
sharpened, what this document anticipated:

- **The byte layer is an interface, not just a piece table.** `IByteSource`
  (`Infrastructure/IByteSource.cs`) is what `RawSegmentIndex`, `RawRowReader` and the new
  `RawRowDecoder` read through; `MMapFile` and `RawPieceTable` both implement it. The contract that
  makes a piece table expressible is that `GetContiguousSpan` may return fewer bytes than asked
  for, truncated at a piece boundary. §1C's "serve replacements at the `GetSpan` chokepoint" idea
  is therefore still available for the JSON view later, but the raw view did not need it.

- **§3's "anchors after X are valid with a constant delta applied. No rescan" is too optimistic,
  and the code does not rely on it.** Byte-capped breaks do sit at fixed byte offsets from a line
  start, so an insert usually leaves later boundaries where they were — but `BreakAtCap` backs off
  up to 3 bytes to avoid splitting a UTF-8 character, so different bytes at the cap give a
  different backoff which chains into the next row; and inserted text containing `\n` splits the
  line outright. `RawEditedRowIndex` therefore walks the original bytes and the edited document
  forward from the same anchor and stops only when they *provably* re-converge: both past every
  edit, offsets differing by exactly the total byte delta, and agreeing on whether a line starts
  there. That last condition is load-bearing rather than belt-and-braces — the byte before a
  convergence point can be the last byte an edit inserted, so a newline inserted onto a soft-wrap
  boundary produces two streams at the same offset that disagree about it
  (`RawEditedRowIndexTests.ConvergenceRequiresAgreementOnWhereLinesStart`).

- **§6's warning about the caret understates one part of it.** The caret's hardest dependency is
  not rendering but decoding: `RawRowReader` is lossy in four directions at once (multi-byte
  collapse, one U+FFFD per invalid run, glyph substitution for controls and for the Unicode
  separators that are not `\n`, trailing newline stripped),
  so a character index says nothing about a byte offset. `RawRowDecoder` produces the map, and
  `RawCaretStops` turns it into legal positions — both of which are byte-layer work with no UI,
  and both of which would otherwise have surfaced halfway through building the view.

## What step 2 has become so far

The raw view has a caret, a selection and copy-out; nothing is editable yet. Three things are
worth recording for whoever picks up typing.

**The ListBox is gone.** `RawTextSurface` draws every visible row itself and implements
`ILogicalScrollable`. §6's warning that the caret is the larger half was right, but the reason is
not the one it gives: the data structure was never the risk, and neither really was the drawing.
The cost was in everything the caret *touches* - focus, scroll extents, the order two scroll
requests arrive in - none of which is caret code.

**Five defects came out of running it on a real multi-GB file, not out of the tests.** They are
listed in [roadmap.md](roadmap.md) §Editing. The common thread is that each lived in the gap
between a component being correct and the application wiring being correct, which is precisely
where a test that constructs its own environment cannot look. The sharpest example: every caret
input test called `Focus()` during setup, so the whole suite passed against an application in
which no key did anything.

**Two of the decisions in §6 were revisited in the light of use.** A reveal centres its row rather
than scrolling minimally, because the target of a jump needs context on both sides; and a reveal
places the caret as well as scrolling, because otherwise the next keystroke acts on wherever the
caret was before a jump across a multi-GB file. Caret movement kept the minimal scroll - the two
are separate operations rather than one with a flag, since centring on every arrow key would leap
half a screen.

Typing, deletion, paste and undo/redo are now done - see "What typing became" below. Still to do
from this step: IME and dead-key composition, which `Avalonia.Headless` cannot test because it
posts finished text rather than composition events.

**Done: a caret position readout.** The caret knew three things the user could not see —
the byte offset into the file, the row and column, and the size of the selection — and on a
multi-GB file the byte offset is the one that matters, because it is what every other tool
(`dd`, a hex editor, a stack trace from a parser) speaks. Both numbers have to be shown rather
than one: a column is a character count over a row's decoded text, a byte offset is a byte count
over the document, and `RawRowDecoder` exists precisely because neither derives from the other.
Selection size should report bytes for the same reason the copy toast does, with the character
count alongside it.

It went in as a status gutter along the bottom of the raw view rather than in the app's status
bar, which is already tight and has no per-view injectable region: a view cannot contribute fields
to it without every view knowing about every other view's fields. `RawCaretReadout` answers the
questions and `RawViewModel` formats them; the gutter borrows the JSON diff view's context-bar
chrome so it reads as the view talking about its selection rather than as part of the document.

The character is named from the file's bytes, not from the row's display text — naming the
substitution glyph instead of the character it stands for would defeat the point of showing it at
all. The name comes from `UnicodeNames`, a table generated from the Unicode Character Database,
because .NET carries categories but no names.

Measured (Apple M5, Release, `RawCaretReadoutBenchmarks`): 448ns and 144B per readout on an
ordinary line, where the allocation is the two strings the gutter displays; 25us at the 1MB column
cap over ASCII and 1.0ms when every rune must be decoded, neither allocating anything further;
21ns and 56B for a name lookup. Through the view model, with all three gutter strings formatted, a
caret move costs ~760B. The name table is 1.6MB resident after its one-time inflate, and a session
that never shows a caret never pays it.

Two numbers are bounded rather than exact, and say so in the gutter. A column is a character count
from the start of the line, and a line here can be a multi-GB minified document, so the scan back
to the line start is capped (`ColumnScanBytes`, 1MB); a selection's character count is capped the
same way (`SelectionScanBytes`), since select-all is one keystroke. A character offset into the
file was dropped outright: it cannot be answered without decoding from byte 0, and the scan that
finds rows never decodes at all, so the number would cost either a full decode per caret move or a
permanently slower index.

The line number is deliberately *not* among the bounded ones. The first cut found the line start by
walking rows back from the caret and gave up at the cap, which lost the line number along with the
column: the gutter showed a column up to 65,537, then "Col —", then nothing at all. Both halves of
that were wrong. `RawSegmentIndex.GetRowInfo` already computes the line number while walking from a
row's anchor and discards it on continuation rows - which is right for a gutter that should leave a
wrapped line unnumbered, and useless to a caret readout - so `IRawRowIndex.LineContaining` reports
it instead, storing nothing and costing one anchor walk. The column then became a byte question
rather than a row question: scan back for a newline with a vectorized `LastIndexOf`, and count
characters with an ASCII fast path over the result. That is what let the cap move from 64KB to 1MB
while getting cheaper - 0.028ms at the cap over ASCII, against a row walk that was giving up
sixteen times sooner.

Edits are gated on a completed scan. The scan's append log is read lock-free precisely because
nothing already written ever changes, and a shift log mutated on the UI thread while the scan
consulted it would end that. The wait is largely notional in the motivating case: revealing a byte
offset already waits for the scan to cover it.

## What typing became

Edit mode is a toggle in the raw view's toolbar, enabled once the scan finishes, and
`RawEditController` is the one thing that sits behind it: it constructs the piece table, the
`RawEditedRowIndex` over it, the `RawEditJournal` and the `RawCaretController`, and every
keystroke reaches the document through it. All four are built there rather than handed in,
because three of them are only meaningful over exactly the fourth's coordinate space, and pairing
a caret with the wrong row index is not a mistake that shows up quickly.

Five things are worth recording.

**A mode, not an always-on editor.** This is the viewer of last resort - what a user opens a 4GB
file in to *look* at it - so a stray keypress silently altering the document would be the worst
possible default. The toggle also gave Escape something to mean: the window's tunnelling Escape
handler, which otherwise dismisses the find bar and pulls focus back to the content area, now
leaves edit mode instead while the raw view is in it, because handling it the old way would have
ended the editing session's focus as well as its mode.

**Deletion is a character question, and `RawCaretStops` already answered it.** Backspace removes
the span between the previous caret stop and the caret, which makes a multi-byte character, a
surrogate pair, a `\r\n` and a whole run of invalid bytes that drew as one U+FFFD each go in one
press - and at the start of a row the previous stop is before the line ending, so backspace there
joins the lines with no special case for it. Nothing in the edit path knows what a character is.

**The order inside one edit is load-bearing at both ends.** `RawEditedRowIndex.ApplyEdit` has to
run before the caret moves, because the caret snaps to a legal position by asking the row index
where the rows are; and the "document changed" notification has to fire before the caret moves,
so whatever caches rows has dropped them by the time the caret's own notification has the view
drawing. Forward delete is the case that shows why the caret cannot be the signal at all: it
leaves the caret exactly where it was, so `RawCaretController` raises nothing, and a view that
redrew only on caret movement would show the deleted character still there.

**§3's "the raw index re-derives" became several dirty spans, not one.** The first cut coalesced
every edit into a single span running from the earliest edit to wherever re-derivation rejoined
the original. That is correct and it is also unusable: it holds every row in between, so changing
two characters a gigabyte apart meant materialising every row of the gigabyte between them, and
the second edit had to be refused outright to stop it. Spans are disjoint now - one per *place*
edited - so the cost tracks how many places, not how far apart they are.

What decides between widening a span and opening a new one is the index's own anchor stride,
not a tuned number. A span can only begin at an anchor, because an anchor is the only place the
original stream's state is known without walking to it, so a new span already pays up to 64 rows
of walking before it reaches the edit; an edit closer than that is cheaper to absorb. Part of the
same rule is not about cost at all: an edit in an anchor bucket the previous span has already
re-derived past *must* join it, or the two spans overlap and every binary search in the class
stops meaning anything. A re-derivation that runs past the span after it swallows that one
instead, so spans stay disjoint and ordered without anyone predicting where a walk will stop.
The anchor boundaries are where all of this changes its answer, so they are tested directly
(`RawEditedRowIndexTests`, the anchor-boundary section) rather than left to the random scripts.

The consequences: editing in one place costs one anchor bucket plus the row or two it takes the
two byte streams to re-converge; going back and forth between two places gives two spans, not
more, because a span is a place and not a keystroke; and what is now unbounded is the *number* of
places, which is what the budget in `NeedsRebuild` is for.

**A span holds anchors, not rows — which is the only reason a long line is survivable.** The
first cut held every row a span derived. That is fine while a span is the sixty-odd rows an
ordinary edit disturbs, and it is not fine at all when it is not: running the editor on the 4GB
test document, a 48-byte edit inside a ~54MB unbroken line produced a span of 676,661 rows and
21MB of `RawRowInfo`, ten times the budget, after which every further edit anywhere in the file
was refused. A span now stores one marker every `AnchorStride` rows and re-walks the bucket on
demand, exactly as `RawSegmentIndex` does over the file — the same edit costs about 10,500
anchors and 170KB.

The shortcut that would avoid the walk entirely is unsound, and it is worth recording because it
is the obvious idea. Inside a soft-wrapped line the breaks fall every `WrapWidth` bytes from the
line start, so it looks as though an insert leaves every later break exactly where it was, and
the span could converge immediately at zero displacement. `RawRowBoundary.BreakAtCap` backs a
forced break off by up to 3 bytes to avoid splitting a UTF-8 character, and which bytes sit at the
cap has just changed — so one different backoff moves the next row, and that chains to the end of
the line. It holds for ASCII and cannot be assumed, which is not a standard a row index gets to
work to. The line really must be walked.

What the walk costs is therefore the residual problem, and it is a time cost rather than a memory
one: roughly 30ns per row (Apple M5, Release, `RawEditKeystrokeBenchmarks.TypeCharactersInsideALongLine`),
so 1.2ms per keystroke inside a 1MB line, 2.1ms at 8MB, 13ms at 32MB, about 20ms at the 54MB line
that prompted this. Laggy at the top of that range, usable, and bounded by the length of the line
rather than of the file. Bounding it properly is what the background re-index is for.

**`NeedsRebuild` is honest about being unimplemented, and about what it is not.** It fires when
the spans together hold more than 65,536 anchors — a megabyte of them, about four million rows of
coverage — and the only thing that happens is that edits in *new* places are refused; editing
where changes already exist keeps working, and lookups stay correct throughout. It is a budget on
what is *kept*, deliberately not on the per-keystroke walk above, which anchors do not bound.

The rebuild it names is worth stating precisely, because the obvious reading of it is wrong. It is
not a re-index of the file: the rows on screen come from the piece table, and the bytes on disk
are a document the user is no longer looking at. A rebuild has to scan the *piece table*, and a
scan is only sound over bytes that then never change - so it is: freeze the current piece table,
scan it into a complete `RawSegmentIndex`, and layer a fresh single-piece `RawPieceTable` over the
frozen one, which becomes the new baseline. That is the "collapses it back to a single piece" in
`RawPieceTable`'s own remarks, and it is a *merge of the edits into the baseline* rather than a
re-read of anything.

Three things fall out of that, and together they are why it is sequenced with save rather than
shipped with typing: edits have to be frozen for the whole scan, which on a multi-GB document is
seconds to minutes of a blocked editor; `RawEditJournal`'s undo snapshots belong to the outgoing
piece table, so undo history either ends at a rebuild or has to learn to cross one; and every
rebuild adds a layer, so each byte read afterwards pays one more binary search. Saving answers the
same problem more cheaply for the case that motivates it, because a save rewrites the file and
starts again from a single piece over it.

**Typing coalesces into one piece, and it did not at first.** Every insertion split the piece it
landed in and described the new bytes as a piece of their own, so four hundred characters typed
became four hundred pieces of one byte each. That is not merely untidy: `GetContiguousSpan`
truncates at every piece boundary, so a row scan across a typed run degenerates from a vectorized
walk into one byte per call, each paying a fresh binary search to be found. An insertion that
continues where the last one ended now grows that piece instead
(`RawPieceTable.TryExtendScratchRun`). The conditions it checks are all about staying
indistinguishable from the insertion it replaces, and one of them is not obvious: an undo rewinds
the piece list but never scratch, which is append-only, so a run's piece can outlive being the
tail of scratch and must not be extended once it has.

**There is an internals inspector, because none of the above is visible from the document.**
Cmd/Ctrl+Shift+D in a Debug build opens `RawEditInspectorWindow`: the piece list, the dirty spans
with both halves of each delta, how full the row index's budget is, the undo depth, and a map
drawing the spans and the pieces as two bars on one horizontal scale — which is what shows the
relationship between them, since every scratch piece sits inside a span and every span exists
because of a scratch piece near it. It refreshes on every keystroke, so an edit's real cost is
something you watch rather than infer.

The whole `Diagnostics` folder is excluded from the build outside Debug (one `Compile Remove` in
the csproj), which is why the window is written in code rather than XAML: a `.axaml` file would
need excluding from `AvaloniaResource` as well, and two rules can disagree. `RawEditSnapshot` and
the `Describe` methods behind it are ordinary compiled code with their own tests, on the grounds
that a debugging aid which lies is worse than not having one — it is trusted at exactly the moment
something else is already confusing. Those tests double as executable statements of the invariants
the window is laid out to make checkable by eye: spans ordered and disjoint, piece logical starts
tiling the document, and the last span's own delta plus everything before it coming to the
document's own.

**Two capabilities are switched off while a piece table exists**, both for the same missing
piece. Re-wrapping is refused, because re-wrapping an edited document means a fresh scan over the
edited bytes - the same background re-index a rebuild needs. And search still reads the file on
disk through the origin, which is §5's recorded decision ("search reflects the last save, and the
document carries a dirty flag that says so"); what is new is that a reveal from a search hit now
also places the caret, so past the first edit its offsets land near rather than on the match. The
status gutter says "Edited — not saved" for the whole of it.

## Related

- [json-array-table-options.md](json-array-table-options.md) — the "export a subtree to file"
  idea noted there is adjacent to this work and shares the streaming-write path.
- [index-memory-analysis.md](index-memory-analysis.md) — the 24 bytes/token index cost quoted
  above, and the field split behind `PackedToken`. The throughput figures are from an
  uncommitted 2026-07-24 profiling harness and are not recorded in the repo.
- [roadmap.md](roadmap.md) — where this sits against everything else deferred.
