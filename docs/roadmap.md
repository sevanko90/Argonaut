# Roadmap - the work still to do

Everything planned, queued or consciously deferred, in one place. Nothing here is built; when
something is, it leaves this file, and what it became is described by the code and
[architecture.md](architecture.md). Items are grouped by area and roughly ordered by value within
a group. Nothing is scheduled.

Two decision records hold the reasoning behind items here that is too long to repeat:
[markdown-options.md](markdown-options.md) and
[store-distribution-comparison.md](store-distribution-comparison.md).

## Waiting on a check by hand

- **The array table on the sparse index.** Open "view as table" on arrays of objects and of
  scalars, expand and collapse column headers, reshape, click cells (scalar and container) and use
  the cell pane's tree.
- **The diff on the tree surface.** Both panes line up while scrolling, with tints, change marks,
  array indices and badges; expand unchanged, added, removed, moved and moved-and-changed rows and
  a range; the context bar's values and paths; next/previous change; "changes only"; find across
  both files; the scrollbar over a long diff.

## Editing

The raw view edits through a piece table and saves by staging a copy beside the file and swapping
it in (see "Saving" in architecture.md).

- **A macOS `IFileReplacer` over `NSFileManager`.** Needed outright for a Mac App Store build: a
  file the user picked grants access to that file, not its folder, so a stage beside it is
  denied. Do what `NSDocument` does - `URLForDirectory(NSItemReplacementDirectory,
  appropriateForURL: destination)` for a staging directory the sandbox permits on the right volume,
  then `replaceItemAtURL:withItemAtURL:`, inside `startAccessingSecurityScopedResource` and ideally
  an `NSFileCoordinator` write so sync clients see one change. Better unsandboxed too: `rename(2)`
  installs a new file, so Finder tags, quarantine and ACLs are lost today (`MacFileReplacer` keeps
  only the mode bits and adds `F_FULLFSYNC`). Needs native interop (Objective-C runtime calls or a
  small shim), the `com.apple.security.files.user-selected.read-write` entitlement, and recent-file
  bookmarks created without the read-only option. Plugs in where `MainWindowViewModel` chooses its
  `IFileReplacer`; `Stage` already takes the destination `IByteOrigin` so a security-scoped grant
  can travel with it, but Save As builds a `FileByteOrigin` from the picked path and would have to
  carry the picker's grant instead. It must meet the rules in architecture.md.
- **IME and dead-key composition.** `Avalonia.Headless` posts finished text rather than
  composition events, so it cannot be tested here.
- **In-memory rebuild when `NeedsRebuild` fires.** Past 524,288 line records (about half a
  million separate places edited) edits in new places are refused with a toast. A save clears this
  more cheaply, so only worth building if that proves not enough.
- **Re-wrap while edited, and search over edited bytes.** Both are off while a piece table exists;
  search reads the file and so lands near rather than on a match past the first edit. A
  merge-iterator over piece-space would be a project of its own.
- **Unicode descriptors elsewhere.** The JSON views could name the character under the cursor the
  way the raw view's readout does.
- **An internals inspector for the JSON indexes**, if the raw one earns its keep.
- **Scalar edits in the JSON tree.** An offset-keyed replacement overlay served at the
  `IByteSource` seam, with no index change. The next decision now that save exists.
- **Structural editing in the JSON tree** (delete, insert, paste): tombstones and fragment indices
  merged into the row walk. The expensive class; not committed to. Saving from raw and re-indexing
  may make it unnecessary.

## JSON tree

- **Per-depth row counts**, only if the estimated scrollbar proves not good enough in use. They
  would give the tree the exact scroll model the raw view has, under the same `RowSurface`
  interface.
- **JSONC comments as rows.** Settings files lean on them, and `JsonTreeReader` skips them as
  trivia. They would be leaf rows in `TreeRunStyle.Comment`. Needs the reader to report a comment
  as a child (with ordinals skipping it, so array indices and paths are unchanged) and the sparse
  index's separator logic to still treat a comment as trivia.
- **Open questions, to settle from measurement:** whether the promotion and checkpoint sizes
  (64 KB each) should scale with file size or a memory budget; whether closing brackets stay rows;
  whether a path resolver that repeats key scans over objects with millions of keys wants a sparse
  key-hash filter per large object; whether `Vector512` is worth a path beside `Vector256`.

## JSON diff

- **Choosing an array's identity key by hand.** Today it is auto-detected only (an `id`-like
  scalar member, unique on both sides); a toolbar choice would cover keys the name rule misses.
- **A block moved into a container that is itself new** is still removed and added: an added
  container is recorded whole, so the similarity pass has nothing inside it to pair with.
- **Windowed alignment of an over-cap array middle**, instead of comparing it in place and ending
  in a range record, if a real diff shows the in-place walk's look-ahead is not enough.
- **Unchanged content shows the left document's spelling in both panes**, so a difference only in
  how a value is written (`1.0` against `1`, escapes, key order inside an unchanged object) is not
  visible. Drawing the right side from its own bytes would need the pairing below the run level.
- **Jumping between a move's two ends** - a click on the stub revealing the destination and back.
- **A changelist summary pane**: a flat list of the changes above the tree, clicking one reveals it.
- **Character-level highlight in the row itself**, not only in the context bar.
- **Two independent schema gutters**, one per document.
- **Diff as a reopenable recent-files entry.**
- **128-bit container hashes.** 64-bit puts a birthday collision around 5×10⁹ nodes; widening
  containers is the escape hatch if it ever matters.
- **Persisting a diff as an RFC 6902 patch.** The records are close to that shape already.

## JSON array table

- **Export a subtree to file.** Wants to be a document-level feature (export any container from the
  tree), not a table button. The write path exists: `ByteSourceReading.WriteTo` into an
  `IFileReplacer` stage, as save uses it.
- **Editing cells.**
- **Sorting and filtering**, including by an expanded column. Either is a full scan, so a feature
  with its own budget; expansion must not quietly become the thing that makes people expect it.
- **Searching within the table.** `CreateSearchNavigator` is where it would land.
- **Opening a remainder column as its own table** - the natural destination for a click on
  `coordinates[…]`. Needs a breadcrumb stack back through the tables; deferred until the cell pane
  proves insufficient.
- **Keeping column expansions across Back and re-entry**, by carrying them in the
  `ArrayTableRequest`. Cheap; not obviously wanted.
- **Cheaper cells.** Viewport column slots made vertical scrolling about 8x faster; the remaining
  cost is most likely `CellTip`, a tooltip built eagerly for every cell (four controls, five
  bindings) for a popup only one cell ever shows. It cannot be built on first hover - Avalonia's
  tooltip service hooks the control when `ToolTip.Tip` is set - so try a lightweight `Tip` value
  plus a shared `DataTemplate` that materialises the visual only when shown. Measure as
  [json-array-table-scroll-perf.md](json-array-table-scroll-perf.md) warns: the first
  configuration measured in a process runs 2-3x slow.

## Schema

- **Binding a schema type at a row**, for documents whose outermost object is not what the schema
  describes: `{"data": {…}, "meta": {…}}` matches no root, though `data` would match a type
  exactly. A small button on a container row samples that node's keys
  (`JsonDocumentKeySampler`), ranks every named root (`JsonSchemaRootMatcher`) and binds the winner
  from there down; if nothing wins clearly it opens the type picker filtered to the candidates,
  rather than binding silently. It also lets the user correct a bad automatic choice in place.
  Mechanism: an offset-keyed override (container value start → schema node) consulted where
  `JsonSchemaResolver` steps from a parent's node to a child's, so a pinned node replaces whatever
  the parent chain gives, including nothing. Overrides are session-only and are cleared when the
  schema or root changes. Pinned rows must say so and be unpinnable. Settle first: whether the
  button lives in the tree row or the schema gutter cell (the gutter is where binding is already
  expressed, but it is narrow and resizable), and whether it shows on every container row or only
  where the row resolves to nothing.

  **The first task decides whether it is built at all: find a document that needs it.** A
  synthetic wrapper around a test payload is enough to build the mechanism, not to judge the UX; a
  real wrapped API response (JSON:API, GraphQL's `{"data": …}`, paginated envelopes) is the real
  test. It has not been needed yet across a real OpenAPI document, a GeoJSON schema and a Keepa
  response, and binding an envelope type as the root may cover most cases. Do not let it grow into
  per-row schema editing.
- **An array root bound as the array's items.** Root matching already scores element 0 of an
  array root and says so, but the winner cannot be bound: `WithRoot` cannot express "root is an
  array of C" without a synthetic node holding `ItemsId = C`.
- **Remember bundled schemas by name, not by path.** `SchemaBindings` stores the absolute path a
  schema was loaded from, and for a bundled schema that is wherever the app ran from - a
  `bin/Debug` copy, an installed bundle - which can be stale or gone after a clean build or a
  reinstall, so a remembered binding silently shows an old schema or none. Store a bundled schema
  by its bundled name and resolve it against the current bundled folder; keep absolute paths for
  the user folder and sidecars.
- **Not planned, stated for completeness:** `oneOf`/`anyOf` branches are merged rather than
  discriminated against values; enum matching is textual; remote `$ref`, `patternProperties` and
  `if`/`then`/`else` are ignored; root matching reads names only, never values.

## Markdown

"Render markdown" is two features: highlighting the source in the raw view, which fits the
existing surface and needs no package, and a rendered preview, which cannot live in the raw view
because fixed-height rows, byte-offset carets and virtualization-by-arithmetic are exactly what
rendering gives up. Reasoning in [markdown-options.md](markdown-options.md).

- **Highlighting in the raw view.** Fence state carried on the index's existing anchors (one bit
  per 64 rows), span classification per visible row, styled runs in `RawTextSurface`.
- **Detection.** `.md`/`.markdown` by extension, plus a corroborated content heuristic: the
  discriminating construct is heading *depth* varying (`#` and `##` in one file), not the hash
  itself; everything else (fence, setext underline, link, table) needs a second distinct signal.
  Worth having before anything renders.
- **A preview view.** Markdig for the AST, our own block renderer over the virtualized list
  pattern, behind a size gate. `Markdown.Avalonia` is the shortcut if a non-virtualized tree is
  acceptable at small sizes.
- **Windowed rendering of large markdown.** Not committed to: link reference definitions and
  footnotes are document-global, so rendering a window still needs a whole-file pre-pass.

## XML

Not designed beyond this. The tree machinery is already format-agnostic -
`SparseContainerIndex`, `TreeCursor` over an `ITreeFormatReader`, `TreeSurface` over an
`ITreeRowSource`, painters as styled runs, gutters as providers - and the test-only S-expression
format keeps it honest. An XML view adds a scanner, a reader and a painter.

- **Detection** in `FileTypeDetector`: the document starts with `<?xml`, or its first characters
  form a tag, after a BOM and whitespace. A bare tag is also how HTML starts, so that case may want
  a second signal.
- **A structural scanner**, span-based over `IByteSource` in the `Utf8JsonReader` mould rather than
  `XmlReader`, which allocates a string for every name and value. It feeds `SparseContainerIndex`
  with elements as containers and checkpoints at child boundaries.
- **Where XML differs from JSON**, and the generic types must allow: close tags are real rows;
  text, comments, CDATA and processing instructions are leaf rows between elements, and a
  checkpoint may land before any of them; resuming at a checkpoint needs the namespace bindings in
  scope, so the reader re-reads each ancestor's start tag (one short read per ancestor); attributes
  sit inline in the element's row, clipped by the display cap, and whether they can also expand as
  child rows is the view's decision.
- **Syntax colouring**: element names, attribute names and values as `TreeRunStyle`s, which exist.

## Input sources

- **Load from URL.** An `HttpByteOrigin`: the download on the background with progress and
  cancellation, reporting `AvailableLength` as bytes land and `LengthSettled` when the response
  completes. The indexers already consume growth, so the work is in the origin. An in-flight source
  cannot be an `MMapFile` - a mapping is a fixed snapshot - so it is in-memory chunks or a
  pre-sized mapping whose written extent is tracked separately (and whose unwritten tail is never
  reported as data - see CLAUDE.md).
- **Ctrl+Shift+V to paste as a new document** while one is open. Plain Ctrl+V pastes only while
  nothing is open, so it can mean "paste into the document" in the raw editor.
- **Reload, or "open containing folder"**, whenever either arrives: it reads `IByteOrigin.Path`
  and disables itself when there is none.
- **`FileOptions.DeleteOnClose` on a temp-file spill.** Disposing a temp-file-backed origin while a
  search is scanning it deletes the file under the scan; the scan already degrades that to a failed
  search, and this would remove even that.
- **Not planned:** a cheaper paste of huge clipboards on Windows. The clipboard can only be read
  whole, so a paste is held in memory regardless (capped at 64 MB); Windows, which has no UTF-8
  clipboard format, also takes a UTF-16 string on the way. The native APIs could avoid it but
  disagree on how - three backends, for a case the cap already bounds.

## Memory and performance

Measure against [index-benchmarks.md](index-benchmarks.md), rerun the same way.

- **Closing a multi-GB file lags.** `MMapFile.Dispose` unmaps a fully-resident view synchronously
  on the UI thread (~43ms per 480MB, so ~400ms at 4.5GB). Moving it off-thread needs a synchronous
  "release visible items" phase before the swap plus a background unmap, and the shell as the sole
  disposal owner to avoid racing the view's detach.
- **Span-based unescape for quoted CSV fields.** `CsvFieldReader.DecodeField` allocates twice for
  a quoted field. Never measured; only if CSV load ever profiles as hot.

## Settings

- **A sandboxed build.** The Mac App Store sandbox gives a stored path no access on relaunch, so
  recent files and schema bindings need security-scoped bookmarks saved with them and resolved when
  read - "a way back to this file" rather than a bare path. Application data moves into the app's
  container, which moves `settings.json` and makes the user schema folder something users cannot
  browse to. The composition root builds the store and the catalog, so the sandboxed build can use
  its own implementations, the way saving sits behind `IFileReplacer`.

## UI

- **Skip the progress bar for work about to finish.** `ProgressBoard` shows anything still
  running after 450ms; it could also project the time left and stay hidden when that is under
  ~300ms. Only if a pointless bar is seen in practice.
- **Toolbar UX pass.** Behaviour and layout, on its own branch. Distinct from toolbar *styling*,
  which was tried and rejected: the default Fluent bordered combos stay.
- **Chrome detail polish.** Chevron animation, indent guides, row hover restyling, a
  search-highlight pill - deferred over rendering-speed concerns on the drawn tree. The compact
  density (22px rows, 16px indent) is settled and not part of this.

## Distribution

Auto-update through Velopack and GitHub Releases is built; GitHub stays the source of truth, and
either store would be an additional channel built from the same tags. Reasoning in
[store-distribution-comparison.md](store-distribution-comparison.md).

- **Microsoft Store (MSIX).** Cheap: full-trust packaging, no sandbox, no code change beyond
  compiling the Velopack update check out of the store build.
- **Mac App Store.** Expensive, mostly for sandbox reasons: the macOS replacer (Editing), the
  sandboxed settings (Settings), the schema sidecar and folder reworked, a second signing identity
  and a yearly fee. Only with a concrete reason to be in that store.
- **Open questions from the Velopack rollout:** a Developer ID and notarization for smoother
  macOS updates (today an update needs manual Gatekeeper approval); whether to keep publishing the
  plain zip once Velopack is proven; a beta channel (`--channel`) if ever wanted. Delta packages
  are off (`--delta None`) for now.
