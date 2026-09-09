# Roadmap — deferred and queued work

A single index of everything that has been consciously deferred, so an idea does not survive only
as a sentence inside a plan document that later gets deleted. Each entry says where the real
detail lives; this file is a pointer list, not a second copy of the reasoning.

Nothing here is scheduled. Items are grouped by area, and roughly ordered by value within a group.

## Editing

Options weighed, and the sequencing, are in [editing-options.md](editing-options.md). Nothing is
built. The decision recorded there is to build the byte layer once in the raw view rather than
starting in the JSON tree, because the raw index is a pure function of the bytes and can be
re-derived where the JSON index cannot.

- **Piece table over (original mapping, append-only scratch).** The byte layer everything else
  needs. Raw view reads move to piece-space; anchor deltas and line-bounded re-flow keep a
  keystroke off the tail of the file. Headlessly testable with no UI.
- **Editing UI in the raw view.** Caret, selection across rows, clipboard, undo/redo. Very likely
  the larger half of the feature — an estimate that prices the data structure and not the caret
  is wrong.
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
