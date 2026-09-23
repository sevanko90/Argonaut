# Roadmap — deferred and queued work

A single index of everything that has been consciously deferred, so an idea does not survive only
as a sentence inside a plan document that later gets deleted. Each entry says where the real
detail lives; this file is a pointer list, not a second copy of the reasoning.

Nothing here is scheduled. Items are grouped by area, and roughly ordered by value within a group.

## Editing

The raw view has an edit mode: a toolbar toggle, enabled once the row scan finishes, behind which
`RawEditController` edits a `RawPieceTable` over (original mapping, append-only scratch). Memory
grows with edits, not file size, and nothing is written to disk until a save. It is in the raw view
rather than the JSON tree because the raw row index is a pure function of the bytes and can be
re-derived where the JSON index cannot. Built:

- Caret, selection across rows, keyboard navigation and copy, drawn by `RawTextSurface`.
- Typing, Enter, backspace/forward delete by character (`RawCaretStops`), paste, and undo/redo
  through `RawEditJournal`; consecutive typing coalesces into one piece.
- Constant-cost edits anywhere, including inside a 100MB+ unbroken line: forced row breaks are
  cap-anchored (see CLAUDE.md), so `RawEditedRowIndex` holds one 12-byte record per edited line.
  ~520ns a keystroke.
- Caret readout gutter: named character under the caret (`UnicodeNames`), byte offset,
  line/column and selection size, with "Edited — not saved" while dirty.
- Edit overview strip beside the scrollbar (`RawEditOverview`), click to jump to an edit.
- Debug-only internals inspector, Cmd/Ctrl+Shift+D (`Diagnostics/RawEditInspectorWindow`).
- Save (Cmd/Ctrl+S) and Save As (Cmd/Ctrl+Shift+S), on a toolbar split button: a background streaming copy
  into a stage beside the file, then unmap, atomic swap (`SiblingFileReplacer`) and a fresh
  re-index with the caret put back. A failed swap leaves the file untouched and the edits open.
  Save / Don't Save / Cancel is asked before closing, opening, pasting, switching view, quitting
  or restarting for an update would drop unsaved edits.

Queued:

- **A macOS `IFileReplacer` over `NSFileManager`.** Needed for a Mac App Store build, and would
  keep Finder tags and ACLs that today's `rename` loses. Plan: [save-plan.md](save-plan.md).
- **IME and dead-key composition.** Deferred past v1; `Avalonia.Headless` posts finished text
  rather than composition events, so it cannot be tested here.
- **In-memory rebuild when `NeedsRebuild` fires.** Past 524,288 line records (~half a million
  separate places edited) edits in new places are refused with a toast. A save clears this more
  cheaply, so a rebuild over the piece table is only worth building if that ever proves not enough.
- **Re-wrap while edited, and search over edited bytes.** Both are off while a piece table exists;
  search reads the file and so lands near rather than on a match past the first edit. Saving brings
  both back; a merge-iterator over piece-space would be a project of its own.
- **Unicode descriptors elsewhere.** The JSON views could name the character under the cursor the
  same way.
- **An internals inspector for the JSON indexes**, if the raw one earns its keep.
- **Scalar edits in the JSON tree.** An offset-keyed replacement overlay served at the
  `IByteSource` seam, with no index change. Now that save exists, this is the next decision.
- **Structural editing in the JSON tree** (delete, insert, paste): tombstones and fragment indices
  merged into the row walk. The expensive class; explicitly not committed to. Saving from raw and
  re-indexing may make it unnecessary.

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
  than as a table button. The write path exists: `ByteSourceReading.WriteTo` into an
  `IFileReplacer` stage, as save uses it.
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

## XML

Not designed yet; this is the starting idea.

- **XML detection.** In `FileTypeDetector`: the document starts with `<?xml`, or its first
  characters form a tag (`<name ...>`). Skip a BOM and leading whitespace first. A bare tag is
  also what HTML starts with, so that case may want a second signal before it wins.
- **A collapsible XML tree view.** Like the JSON tree, with elements as the collapsible nodes in
  place of `{}`/`[]`, virtualized the same way.
- **An XML structure index.** Built on the background like `JsonStructureIndex`: per node its
  kind, depth, byte offsets, end index for skipping subtrees, and attributes. Needs a span-based
  scanner over `IByteSource` in the `Utf8JsonReader` mould rather than `XmlReader`, which
  allocates a string for every name and value. Decide how comments, CDATA, processing
  instructions and mixed text content show up as rows.
- **Syntax colouring.** Separate colours for element names, attribute names and attribute values.

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
  user folder's schemas with no sidecar and nothing preselected. Save follows the same rule the
  other way: with no `Path` it becomes Save As. There is no reload or "open containing folder" yet;
  whenever one arrives it reads `Path` and disables itself when there is none.
- **One accepted edge case.** Disposing a temp-file-backed origin while a search is still scanning
  it deletes the file under that scan. `SearchSession.Scan` already catches every exception into
  `OpenFailure` ("an unreadable/vanished target is an outcome rather than a fault"), so it
  degrades to a failed search rather than a crash. `FileOptions.DeleteOnClose` on the spill would
  remove even that.

## Settings

No plan document yet. Flagged during the 2026-09 project restructure, and left out of it on
purpose because it needs its own rework rather than a move.

- **A settings service behind an interface.** Today each preference is a static class that owns
  a file name, a private record and its own `Load`/`Save`, and writes its own JSON file through
  the static `JsonSettingsStore`: `theme.json`, `font.json`, `expand-depth.json`,
  `raw-wrap-width.json`, `schema-selection.json`, `recent-files.json`, `auto-update.json`. Nine
  files across the Shell and the features call those statics directly. What that costs:
  - **No seam.** Nothing can be substituted, so tests redirect the real disk through the static
    `AppDataPaths.RootOverride`. That global forces the 14 test classes that touch settings into
    one serial xUnit collection (`AppDataPaths`).
  - **No cache or notification.** Every `Load` re-reads its file, every `Save` writes
    synchronously on the caller's thread (usually the UI thread), and nothing tells anyone else
    that a value changed. Each caller keeps its own copy in sync by hand.
  - **The wrong layer.** Feature and shell preferences (`RawWrapWidthPreference`,
    `SchemaSelectionPreference`, `ThemePreference`, ...) sit in `Engine/Settings` because the
    storage helper does. Engine should hold only the mechanism.

  Rough shape: an `ISettingsStore` in `Engine/Settings` (load once, serve from memory, write
  behind on the background, raise a change event), an in-memory implementation for tests (which
  removes `RootOverride` and the serial collection), and typed preference sections owned by
  whoever uses them (Raw owns the wrap width, Json the expand depth and schema binding, the Shell
  the theme, font, recent files and auto-update). Open questions: one file or one per section;
  reading the existing per-file JSON on first run so nobody loses their settings; and where the
  schemas folder path goes, since it is a location rather than a setting.

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

- **Skip the progress bar for work about to finish.** `ProgressBoard` shows anything still
  running after 450ms. It could also project the time left from the progress reported so far and
  stay hidden when that is under ~300ms - the slow-start, fast-finish case the delay alone still
  shows. Worth adding only if a pointless bar is seen in practice.

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
