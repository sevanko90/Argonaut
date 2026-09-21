# Roadmap — deferred and queued work

A single index of everything that has been consciously deferred, so an idea does not survive only
as a sentence inside a plan document that later gets deleted. Each entry says where the real
detail lives; this file is a pointer list, not a second copy of the reasoning.

Nothing here is scheduled. Items are grouped by area, and roughly ordered by value within a group.

## Editing

Options weighed, and the sequencing, are in [editing-options.md](editing-options.md). The decision
recorded there is to build the byte layer once in the raw view rather than starting in the JSON
tree, because the raw index is a pure function of the bytes and can be re-derived where the JSON
index cannot. Steps 1 and 2 are built: the raw view has a caret, a selection, copy-out and an
edit mode in which typing changes the document. Step 3 - saving - is the next piece of work, and
until it lands an edited document cannot be written back.

- ~~**Piece table over (original mapping, append-only scratch).**~~ **Built** (`Features/Raw/`:
  `RawPieceTable`, `RawEditedRowIndex`, `RawRowDecoder`, `RawCaretStops`, `RawEditJournal`,
  `RawTextExtractor`, over the `IByteSource` seam). Raw reads are in piece-space; the row index
  re-derives only the span an edit disturbed and reports `NeedsRebuild` when that span grows past
  its threshold. No UI change — verified headlessly against a from-scratch index of the edited
  bytes. Measured: ~1.6-12.5ns per offset resolution (1 to 1024 pieces, no allocation), ~10us per
  keystroke including re-derivation.
- **Editing UI in the raw view.** *Built*, apart from IME. `RawTextSurface` replaced the
  `ListBox`: it draws every visible row itself, implements `ILogicalScrollable`, and holds the
  caret (`RawCaretController`, `RawCaret`), selection across rows, and copy. Edit mode is a
  toolbar toggle gated on `RawSegmentIndex.AllItemsPublished`, and `RawEditController` is what
  sits behind it — typing, Enter, backspace and forward delete against the piece table, paste,
  and undo/redo through `RawEditJournal`. `MainWindow`'s tunnelling Escape leaves edit mode
  rather than dismissing the find bar while the raw view is in it. IME/dead-key composition is
  deferred past v1 — `Avalonia.Headless` posts finished text rather than composition events, so
  it cannot be tested here.

  The prediction that the caret would be the larger half held. Five defects came out of running
  it on a real 4GB file rather than out of the test suite, and each is worth remembering because
  the tests could not have found them:
  - The surface never took keyboard focus. Every input test called `Focus()` in its own setup, so
    all of them passed against an app where none of the keyboard worked.
  - A cached scroll extent went stale between the row count growing and anything refreshing it,
    so a reveal clamped ~1.6M rows short on a 4GB file. Extents are computed live now.
  - Placing the caret before revealing let the caret's own minimal scroll park the row on the
    bottom edge, after which the centred reveal found it "already visible". Two correct behaviours
    cancelling out; ordering is load-bearing and now tested end to end.
  - The caret was drawn over the full row height rather than the text's, so it overhung the glyphs.
  - Find highlighting could not span a soft wrap (pre-existing, inherited from the attached-property
    version it replaced).
- ~~**More than one dirty span in `RawEditedRowIndex`.**~~ **Built.** Edits no longer coalesce
  into a single span from the earliest edit to the re-convergence point; spans are disjoint, one
  per *place* edited, so two edits a gigabyte apart cost two anchor buckets rather than every row
  in between. Widen-or-open is decided by the index's own `AnchorStride` rather than a tuned
  number, and part of that rule is a correctness guard (an edit in an anchor bucket the previous
  span already covers must join it, or the spans overlap) rather than a preference.
- ~~**A window onto the editor's internals.**~~ **Built**, Debug only: Cmd/Ctrl+Shift+D opens
  `Diagnostics/RawEditInspectorWindow` — piece list, dirty spans with both halves of each delta,
  budget fullness, undo depth, and a map drawing spans and pieces on one scale, refreshed per
  keystroke. The folder is excluded from non-Debug builds; the `RawEditSnapshot` behind it is
  ordinary tested code. Nothing equivalent exists for the JSON indexes, which is the obvious
  place to take this next if it earns its keep.
- ~~**Editing inside a very long line holds every row of it.**~~ **Fixed**, and the fix was not
  the one first written here. Found by running the editor on the 4GB test document: a 48-byte edit
  inside a ~54MB unbroken line produced a span of 676,661 rows and 21MB of `RawRowInfo`, ten times
  the budget, after which every further edit anywhere was refused.

  The fix first proposed — converge immediately at zero displacement, since a soft-wrapped line
  breaks every `WrapWidth` bytes from the line start — is **unsound**, and the note is kept because
  it is the obvious idea: `RawRowBoundary.BreakAtCap` backs a forced break off up to 3 bytes to
  avoid splitting a UTF-8 character, which bytes sit at the cap has just changed, and one different
  backoff chains. The line genuinely has to be walked.

  What was avoidable is *keeping* every row it walks past. A span now stores one anchor every
  `AnchorStride` rows and re-walks the bucket on demand, exactly as `RawSegmentIndex` does — the
  same edit costs about 10,500 anchors and 170KB. What remains is a time cost of roughly 30ns per
  row walked, so about 20ms per keystroke at 54MB; measured across line lengths in
  `RawEditKeystrokeBenchmarks.TypeCharactersInsideALongLine`, and bounded properly only by the
  re-index below.
- **Re-index over the piece table, for when `NeedsRebuild` fires.** The budget is now the only
  cap: past 65,536 derived rows — about a thousand separate places edited, or one edit re-flowing
  a very long line — `CanAbsorbEditAt` refuses to open a span somewhere new
  (`RawEditOutcome.NoRoomForAnotherEditSite`, a toast), while editing where changes already exist
  keeps working and every lookup stays correct.

  The rebuild is **not** a re-index of the file, and the distinction is the whole difficulty: the
  rows on screen come from the piece table, so the scan has to run over the piece table, and a
  scan is only sound over bytes that then never change. The shape is freeze the piece table, scan
  it, and layer a fresh single-piece `RawPieceTable` over the frozen one as the new baseline — a
  merge of the edits into the baseline, which is what `RawPieceTable`'s "collapses it back to a
  single piece" means. Hence the sequencing with save: edits are frozen for the whole scan
  (seconds to minutes on a multi-GB document), `RawEditJournal`'s snapshots belong to the
  outgoing table so undo history either ends at a rebuild or must cross one, and each rebuild adds
  a layer that every later read pays a binary search for. A save rewrites the file and starts
  again from one piece over it, which answers the motivating case more cheaply.
- ~~**Caret position readout.**~~ **Built** as a status gutter along the bottom of the raw view
  (`RawCaretReadout`, `RawView.axaml`): the character under the caret named in full on the left
  (`UnicodeNames`, a generated Unicode Character Database table), and byte offset, line/column and
  selection size on the right. It went in the view rather than the app's status bar, which is tight
  and has no per-view injectable region.

  Deliberately **not** included: a character offset into the file. It cannot be answered without
  decoding from byte 0, and the row scan finds breaks with a vectorized newline search that never
  decodes — so the number would cost either a full decode per caret move or a permanently slower
  index. The column and the selection's character count are capped for the same reason
  (`ColumnScanBytes` 1MB, `SelectionScanBytes`) and report "—" past it; the line number is not
  capped, since `IRawRowIndex.LineContaining` gets it from the anchor walk the index already does.
- **Unicode descriptors elsewhere.** The name lookup is not raw-specific; the JSON views could
  identify a character under the cursor the same way.
- **Save as a streaming rewrite.** Staged temp file, atomic swap, background re-index. The copy is
  one sequential pass and not where the difficulty lives; the swap is platform code behind
  `IFileReplacer`, because the Mac App Store sandbox forbids a temp file beside the original and
  Windows forbids replacing a file that is still mapped. Design in
  [editing-options.md](editing-options.md) §4.
- **Scalar edits in the JSON tree.** An offset-keyed replacement overlay served at
  the `IByteSource` seam, with no index change, is a small self-contained feature on its own. Decide
  it *after* the raw editor ships, not before.
- **Structural editing in the JSON tree** (delete, insert, paste) — tombstones and fragment
  indices merged into the row walk. The expensive class. Explicitly not committed to.
- **Search while a document is dirty.** The chosen answer is that search reflects the last save
  and the document says so; a merge-iterator over piece-space is a project of its own. Now that
  edit mode exists this has a visible consequence: a reveal places the caret as well as
  scrolling, so past the first edit a search hit lands near the match rather than on it. The
  raw view's status gutter carries the "Edited — not saved" marker throughout.

## JSON diff

Detail: [json-diff-plan.md](json-diff-plan.md) §"Deferred to follow-ups".

- **Similarity pairing for moved-and-edited containers.** The highest-value item on this list.
  Designed in Stage 2 of the diff plan and sequenced after v1; without it a block that was both
  relocated and edited loses its interior diff entirely.
- **Two independent schema gutters**, one per document.
- **Diff as a reopenable recent-files entry.**
- **128-bit container hashes.**
- **Persisting a diff as an RFC 6902 patch.** The diff records are already close to this shape.

## JSON array table

The feature shipped (`Argonaut/Features/Json/JsonArrayTableView*`); these were held back from it.
Options weighed and discarded are in [json-array-table-options.md](json-array-table-options.md)
and [json-array-nesting-options.md](json-array-nesting-options.md).

- **Export a subtree to file.** Carried over from an earlier feature list, and the reason the
  table has no "export this back out" action: the useful version of it is a document-level feature
  (export any container from the tree, not just a table), so it wants sizing on its own rather
  than as a table button. Shares the streaming-write path with
  [editing-options.md](editing-options.md) §4.
- **Editing cells.**
- **Sorting and filtering the table.**
- **Searching within the table.** `CreateSearchNavigator` is where this would land.
- **Real column virtualization.** Cell reuse on horizontal scroll bought most of the win already;
  the remaining gap and the measurements behind it are in
  [json-array-table-scroll-perf.md](json-array-table-scroll-perf.md), which fingers `CellTip` —
  a tooltip built eagerly for every cell — as the next thing to try.

## Schema

- **Binding a schema type at a row**, for documents whose outermost object is not the thing the
  schema describes. Full design in [schema-row-bind-plan.md](schema-row-bind-plan.md); it is
  deferred for want of a document that exhibits the problem, and finding one is the first task and
  the one that decides whether it gets built at all.

## Markdown

Options weighed in [markdown-options.md](markdown-options.md). "Render markdown" is two features:
highlighting the source in the raw view, which fits the existing surface and needs no package, and
a rendered preview, which cannot live in the raw view because fixed-height rows, byte-offset carets
and virtualization-by-arithmetic are exactly what rendering gives up.

- **Markdown highlighting in the raw view.** Fence state carried on the index's existing anchors
  (one bit per 64 rows), span classification per visible row, styled runs in `RawTextSurface`.
  No dependency, no new view.
- **Markdown detection.** `.md`/`.markdown` by extension, plus a corroborated content heuristic.
  The discriminating construct is heading *depth* varying (`#` and `##` in one file), not the hash
  itself — `#` is the comment character of half the languages in use, and `**` is emphasis in
  several others. Everything else (code fence, setext underline, link, table) needs a second
  distinct signal. Worth having even with nothing to render yet.
- **A markdown preview view.** Markdig for the AST, our own block renderer over the virtualized
  list pattern, behind a size gate. `Markdown.Avalonia` is the shortcut if a non-virtualized tree
  is acceptable at small sizes.
- **Windowed rendering of large markdown.** Explicitly not committed to. Link reference definitions
  and footnotes are document-global, so rendering the visible window still needs a whole-file
  pre-pass - two indexes, not one - and giant markdown is not the common artefact giant JSON is.

## Input sources

Both halves of the seam are now in place.

**Reading** goes through `IByteSource`: every consumer is typed to it, the whole-range read is
`ByteSourceReading.RequireContiguous`, and `AvailableLength`/`LengthSettled`/`WaitForLength` let a
scan index bytes that are still arriving. `MMapFile` survives only as the file-backed
implementation.

**Creation and identity** go through `IByteOrigin`: it owns where the bytes came from (a display
name, a path on disk or null) and hands out `IByteSource`s over them - `Open()` for the whole
document, `OpenRange()` for a sub-document, one search chunk, or an NDJSON line. Implemented by
`FileByteOrigin` and `MemoryByteOrigin`. The shell owns one per open input (two when diffing) and
an origin's lifetime is the **input**, not the view, so a view swap re-opens a source over the
same origin rather than re-materialising it.

- **Paste from clipboard — done.** `MainWindowViewModel.PasteAsync` reads the clipboard through
  an injected delegate, builds a `MemoryByteOrigin`, and runs it through the same
  `OpenOriginAsync` a file takes, so detection is on the bytes (a paste has no extension) and
  every path-keyed feature skips it. Reachable from the empty state's "Paste data" button and
  Ctrl+V — the shortcut deliberately only while nothing is open, so that plain Ctrl+V inside the
  raw editor can mean "paste into the document" once editing is wired up. Ctrl+Shift+V as a
  paste-as-new-document that works with a document open is the obvious extension.

  **No spill to a temp file, at any size** - and not because of a threshold judgement, because
  the clipboard cannot be read any other way. Avalonia's API has no incremental read and no way
  to ask the size first: `IAsyncDataTransferItem.TryGetRawAsync` hands over the whole payload in
  one allocation. So the process is holding those bytes regardless, and a spill would only change
  whether they sit in managed memory - paid for with a temp file's lifetime, its deletion
  ordering, and the Windows "cannot delete a mapped file" hazard. `MaxPasteBytes` (64 MB) is a
  sanity bound, not a spill threshold; past it the paste is declined with a message pointing at a
  file. A clipboard will hold far more than 64 MB.

  What *is* worth doing, and is done: `MainWindow` inspects `IAsyncDataTransfer.Formats` (which
  needs no fetch) and prefers a platform format that yields UTF-8 bytes -
  `public.utf8-plain-text` on macOS, `text/plain;charset=utf-8` on X11/Wayland - falling back to
  `TryGetTextAsync` otherwise. That skips a whole representation on those platforms: no UTF-16
  string (two bytes per ASCII character, on the large object heap at any size worth worrying
  about) and no transcode to the UTF-8 the indexers read. Windows has no standard UTF-8 clipboard
  format, so it takes the string path.

  **If the doubled copy on Windows ever matters**, the native APIs can do better and disagree
  about how: Windows can report an `HGLOBAL`'s size with `GlobalSize` before copying anything and
  lets you transcode from the locked pointer in chunks; X11's INCR protocol and Wayland's file
  descriptor are genuinely incremental but tell you nothing about the size up front; macOS gives
  you an `NSData` whole. Exploiting that means three native backends plus the clipboard-locking
  and delayed-rendering edge cases, for a case the cap already bounds - so it is deliberately not
  done.
- **Load from URL.** Needs an `HttpByteOrigin`: the download on the background with progress
  through `IProgressReporter` and cancellation, reporting `AvailableLength` as bytes land and
  `LengthSettled` when the response completes. The indexers already consume growth, and
  `Utf8JsonReader`'s `isFinalBlock`/`JsonReaderState` resumption is the streaming primitive, so
  the work is in the origin rather than in the readers. Note a mapping is a fixed snapshot of a
  byte range and can never grow, so the in-flight source cannot be an `MMapFile` - it is either
  in-memory chunks or a pre-sized mapping whose written extent is tracked separately (and whose
  unwritten tail must never be reported as data - see CLAUDE.md).
- **Path-keyed features already degrade.** The three that exist - recent files, the
  `<file>.schema.json` sidecar and the remembered schema binding - consult `IByteOrigin.Path` and
  skip when it is null; `JsonSchemaCatalog.GatherForDocument` takes a null path and offers the
  user folder's schemas with no sidecar and nothing preselected. There is no save, reload or
  "open containing folder" in the app yet, so there is no enabled state to drive; whenever one of
  those arrives it reads `Path` and disables itself when there is none.
- **One accepted edge case.** Disposing a temp-file-backed origin while a search is still scanning
  it deletes the file under that scan. `SearchSession.Scan` already catches every exception into
  `OpenFailure` ("an unreadable/vanished target is an outcome rather than a fault"), so it
  degrades to a failed search rather than a crash. `FileOptions.DeleteOnClose` on the spill would
  remove even that.

## Memory and performance

Detail: [perf-review-2026-07-17.md](perf-review-2026-07-17.md).

- **Halve the NDJSON line index by storing offsets, not spans.** `FileLineSpan` is 16 bytes;
  lines are contiguous, so a length is derivable from the next line's offset. A `List<long>` of
  line starts is 8 bytes per line. Not `uint` — 4GB files sit exactly at the wraparound.
- **Delta + varint encoding for `PackedToken`** (option 4 of
  [index-memory-analysis.md](index-memory-analysis.md), never implemented). Would take the index
  from 24 bytes/token to an estimated 8-12 average. Judged worth pursuing whenever the
  ~2.24 GiB-per-100M-token baseline needs to shrink further: the added random-access cost is
  low microseconds and the complexity stays in decode logic rather than threading.
- **Span-based unescape for quoted CSV fields.** `CsvFieldReader.DecodeField` allocates twice for
  a quoted field (a `Replace("\"\"", "\"")` after the initial decode). Noted from reading the
  code, never measured — a candidate only if CSV load ever profiles as hot.

Retired: dropping `JsonTokenInfo.ParentIndex` (perf review item 7) is no longer available. It was
proposed when only tests read it; `JsonPathBuilder`, `JsonPathResolver`, `JsonArrayElementIndex`,
`JsonDiffRowCollection` and `JsonVisibleRowCollection` all walk the parent chain now, so the
4 bytes per token are earning their keep.

## UI

- **Toolbar UX pass.** Behaviour and layout, on its own branch rather than folded into a feature
  branch. Distinct from the toolbar *styling* that was tried and rejected: borderless tinted pill
  combos are not wanted, the default Fluent bordered combos stay.
- **Chrome detail polish.** Chevron animation, indent guides, row hover restyling, a
  search-highlight pill. Deferred over rendering-speed concerns on the virtualized tree; a
  perf-conscious plan exists outside the repo. The compact tree density (22px rows, 16px indent)
  is a settled choice and is not part of this.

## Distribution

Auto-update through Velopack and GitHub Releases is built (see
[velopack-auto-update-plan.md](velopack-auto-update-plan.md)); the store channels are not.
Detail: [store-distribution-comparison.md](store-distribution-comparison.md).

- **Microsoft Store (MSIX).** Cheap: full-trust packaging, no sandbox, no code change beyond
  compiling the Velopack update check out of the store build.
- **Mac App Store.** Expensive, and mostly for sandbox reasons rather than packaging:
  security-scoped bookmarks for recent files and remembered schemas, the schema sidecar and
  schema folder reworked, and a sandboxed save implementation behind `IFileReplacer`. Only
  worth it with a concrete reason to be in that store.
