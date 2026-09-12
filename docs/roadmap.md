# Roadmap — deferred and queued work

A single index of everything that has been consciously deferred, so an idea does not survive only
as a sentence inside a plan document that later gets deleted. Each entry says where the real
detail lives; this file is a pointer list, not a second copy of the reasoning.

Nothing here is scheduled. Items are grouped by area, and roughly ordered by value within a group.

## Editing

Options weighed, and the sequencing, are in [editing-options.md](editing-options.md). The decision
recorded there is to build the byte layer once in the raw view rather than starting in the JSON
tree, because the raw index is a pure function of the bytes and can be re-derived where the JSON
index cannot. Step 1 is built and step 2 is part-built: the raw view now has a caret, a selection
and copy-out. Nothing is editable yet - typing is the next piece of work.

- ~~**Piece table over (original mapping, append-only scratch).**~~ **Built** (`Features/Raw/`:
  `RawPieceTable`, `RawEditedRowIndex`, `RawRowDecoder`, `RawCaretStops`, `RawEditJournal`,
  `RawTextExtractor`, over the `IByteSource` seam). Raw reads are in piece-space; the row index
  re-derives only the span an edit disturbed and reports `NeedsRebuild` when that span grows past
  its threshold. No UI change — verified headlessly against a from-scratch index of the edited
  bytes. Measured: ~1.6-12.5ns per offset resolution (1 to 1024 pieces, no allocation), ~10us per
  keystroke including re-derivation.
- **Editing UI in the raw view.** *Part-built.* `RawTextSurface` replaced the `ListBox`: it draws
  every visible row itself, implements `ILogicalScrollable`, and holds the caret
  (`RawCaretController`, `RawCaret`), selection across rows, and copy. Still to do: **typing and
  deletion** wired to the piece table, edit mode gated on `RawSegmentIndex.IsComplete`, undo/redo
  wired to `RawEditJournal` (built, unused), paste, and making `MainWindow`'s tunnelling Escape
  handler mode-aware. IME/dead-key composition is deferred past v1 — `Avalonia.Headless` posts
  finished text rather than composition events, so it cannot be tested here.

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
- **Save as a streaming rewrite.** Temp file beside the original, atomic rename, background
  re-index. One sequential pass; not where the difficulty lives.
- **Scalar edits in the JSON tree.** An offset-keyed replacement overlay served at
  `MMapFile.GetSpan`, with no index change, is a small self-contained feature on its own. Decide
  it *after* the raw editor ships, not before.
- **Structural editing in the JSON tree** (delete, insert, paste) — tombstones and fragment
  indices merged into the row walk. The expensive class. Explicitly not committed to.
- **Search while a document is dirty.** The chosen answer is that search reflects the last save
  and the document says so; a merge-iterator over piece-space is a project of its own.

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
