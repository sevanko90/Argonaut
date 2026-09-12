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

## Input sources

Today a document is always a path: every load site builds an `MMapFile` from one, and the
`IByteSource` seam it implements stops at the read API — `IndexedFileSession.File` is typed as the
concrete `MMapFile`, and search, the NDJSON sub-document view and the array table all re-map the
*path* to get an independent view of a byte range.

- **Paste from clipboard, and load from URL.** Both are the same piece of work: widen the seam from
  "a mapped file" to a byte-span emitter that does not care where the bytes came from. Rename/extend
  `IByteSource` into an `IDataProvider` that owns the source's identity as well as its bytes
  (length, a display name, whether it has a path on disk, and how to derive a sub-range provider for
  [offset, length) without going back to a path), then implement it three ways: the existing mapped
  file, a `ClipboardDataProvider` over an in-memory array, and an `HttpDataProvider` that streams the
  response. The work is mostly in the call sites, not the interface: `IndexedFileSession<TIndex>` and
  every view model that constructs `new MMapFile(path)` move to taking a provider, and the three
  places that re-map by path (`FileSearchSession`'s chunk views, `JsonArrayTableSession`,
  `JsonViewModel`'s sub-document load) must ask the provider for the sub-range instead.

  Two decisions to make when it is picked up, not now:
  - **Where large non-file payloads live.** A clipboard paste or a download above some threshold
    should spill to a temp file and be served by the ordinary mapped-file provider, so the multi-GB
    path stays exactly the one that is already tuned; only small payloads stay as a pinned array.
    That keeps `Length` OS-reported for everything big (see CLAUDE.md) and costs one copy.
  - **What the path-shaped features do without a path.** Recent files, save-as, reload and "open
    containing folder" all assume one exists. The provider needs to say so, and the UI needs to
    degrade rather than each site guarding on a null path.

  The HTTP provider also wants the download itself on the background with progress reported through
  `IProgressReporter` and cancellation off `IDocumentSession.TearingDown`, so a slow or wedged URL is
  no different from a slow index.

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
