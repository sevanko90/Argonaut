# JSON tree: a sparse index and a drawn surface

The JSON tree's memory grows with token count, not file size. `JsonStructureIndex` holds a 24-byte
`PackedToken` for every token, End tokens included, plus 8 bytes a token when content hashes are on.
JSON tokens are 2-15 bytes, so the index is 2-3x the file for typical nested documents and ~12x for
token-dense ones (`[1,2,3,…]`); a 1GB file profiles at ~2.8GB of heap. No packing fixes this: any
per-token record is linear in tokens, and at 2 bytes a token even a 2-byte record matches the file.
The index has to be sparse - anchors to jump into the file, with everything between them re-parsed
on demand.

The view is the other half. The tree is a `ListBox` over `JsonVisibleRowCollection`, an `IList`,
and an `IList` needs an exact `Count` and random access to row *i* of the flattened expanded tree.
That demand is what makes a dense, globally numbered row space look necessary. A drawn surface
like `RawTextSurface` needs only an anchor, a cursor and an extent estimate, which is exactly what a
sparse index provides. The two changes are done together; either one alone keeps most of the cost.

## Goals

- Index memory a small, bounded fraction of the file (target: under 1%), for any JSON shape.
- First paint without waiting for any indexing.
- No per-container child caps, no "show more", no full rebuild on expand/collapse.
- Live view during indexing with no `CollectionChanged` churn.
- Every existing consumer (paths, search, schema, hints, array table, diff, NDJSON line pane) keeps
  working.

## The index

**Large containers only.** A container is recorded only if its byte span is at least `T`
(default 64KB): start, end, child count, parent, depth - about 32 bytes. Smaller containers and all
scalars are never stored; parsing up to `T` bytes when a row is realised costs microseconds.
Large containers with no large child are disjoint, so there are at most `F/T` of them; only
single-child chains (`[[[[…]]]]`) add more, and a depth cap on what is recorded bounds those.

**Child checkpoints inside large containers.** At the first child boundary after every `B` bytes
(default 64KB), record `(offset, childOrdinal)`. A checkpoint is always a value start inside a
known container, so the parser resumes with its ancestor stack taken from the large-container
chain - never mid-token, never by reconstructing `JsonReaderState`. `arr[5_000_000]` is a binary
search on ordinals and a skip of at most `B` bytes of siblings; a large sibling is skipped by its
recorded end.

**Size.** For 1GB at the defaults: ~16K container records and ~16K checkpoints, about 1MB.
`T` and `B` may later be derived from a memory budget rather than fixed.

**Optional per-depth row counts.** Each checkpoint can carry cumulative token counts per relative
depth 0..15 (64 bytes a checkpoint). With them the row count for any uniform expand depth is exact
and the scrollbar row-accurate; overrides adjust by the toggled container's own counts. The surface
does not need these - an estimated extent works - so they are an increment, not a prerequisite.

**Append-only and growth-safe.** Both logs are append-only, so the rules in CLAUDE.md about
`AvailableLength`, `LengthSettled` and `WaitForLength` apply unchanged. A container's end is
written once when it closes, the same publish-then-mutate pattern `EndIndex` uses today.

**Identity is a byte offset.** A row is `(offset, kind)`: a container's start, a scalar's start, or
`(end offset, Close)` for a closing bracket. Expand overrides, selection and bookmarks are keyed by
offset, which is stable under growth and shared with the raw view and search without translating
through token numbers. There is no `TokenIndex`, `ParentIndex` or `EndIndex`: parents come from the
cursor's stack and ends from a container record or a skip scan within `T`.

## The structural scanner

Skipping and re-parsing only stay cheap if they run at GB/s, faster than `Utf8JsonReader`. Use the
simdjson stage-1 approach over `Vector256`/`Vector512`: bitmasks of quotes, backslashes and
structural characters, with string interiors masked out by a carry-less-multiply prefix XOR
(PCLMULQDQ on x64, PMULL on ARM). Skipping a subtree is then depth counting over the structural
mask. `Utf8JsonReader` stays for decoding the rows that are shown.

The stage-1 scan does not fully validate. The index build validates in the same pass and keeps only
the first error offset, so the failure reporting `DescribeFailure` does today still has an offset
to report.

## The surface

A drawn control in the pattern of `RawTextSurface`: `ILogicalScrollable`, fixed row height, the
visible range computed from the scroll offset, and one `Render` pass for text, expand toggles,
selection, find highlights, schema gutter and hint badges.

- **Anchor**: `(offset of first visible row, pixel sub-offset)`.
- **Cursor**: a `JsonRowCursor` with `MoveNext`/`MovePrevious` over display rows under the current
  expand state, carrying the ancestor stack - the JSON counterpart of `RawRowCursor`.
- **Forward** is a plain parse from the anchor, so rendering needs no index at all; the index is a
  seek accelerator.
- **Backward** re-parses from the previous checkpoint (at most `B` bytes). Decoded rows are cached
  per checkpoint span in a small LRU of blocks, so memory follows the viewport, not the file.
- **Seek** (thumb drag, search hit, path navigation, go to parent) descends the large-container
  chain to the nearest checkpoint and parses forward.
- **Extent** is an estimate - byte-proportional, or exact where per-depth counts exist. When the
  estimate changes, the anchor stays put and the correction goes into the offset, so content never
  jumps (the CodeMirror 6 approach to estimated heights).
- **Expand state** stays "default depth XOR override", with the overrides in an ordered set of
  container offsets.
- **Sticky ancestor headers** fall out of the cursor knowing its ancestor chain, and are worth
  having at multi-GB depths.

Shared machinery moves down rather than sideways: scroll arithmetic, row height, horizontal pan,
gutters and caret/selection plumbing go to `Ui/RowSurface`, used by both `RawTextSurface` and the
tree surface. Everything tree-shaped goes one level further, into a format-agnostic tree layer the
XML view will reuse - see the next section.

What the `ListBox` gave for free has to be built: keyboard navigation, focus, hit testing, context
menus, tooltips and automation peers. Accessibility is the one most likely to be forgotten, so it is
part of the surface work, not a follow-up. Row templates become draw code; cache a `TextLayout` per
visible row.

## Shared with the XML view

The roadmap's XML tree is the same problem: collapsible nodes over a multi-GB file, with elements
where JSON has `{}`/`[]`. It must not become a third fully custom view, so everything that is about
*a tree of byte ranges* is format-agnostic from the start, and a format supplies only a scanner, a
cursor and a row painter.

**Generic, in `Engine/Indexing/Trees`** (no Avalonia, beside `Engine/Indexing/Lines`):

- **`SparseContainerIndex`**: the large-container records and child checkpoints above. It knows
  byte spans, ordinals, parents and depths, never JSON. A format's scanner feeds it open, close and
  child-boundary events through a narrow sink interface, and it applies `T`, `B`, the depth cap and
  the growth rules.
- **`ITreeRowCursor`**: `MoveNext`, `MovePrevious`, `SeekTo(offset)`, and the current row as
  `(offset, depth, shape, formatKind)`, where shape is `Open`, `Close` or `Leaf` and `formatKind` is
  a byte only the format interprets. `JsonRowCursor` implements it; an XML cursor would too.
- **Expand state**: default depth XOR offset-keyed overrides.
- **Row block cache and extent estimate**, including the anchor-preserving correction.

**Generic, in `Ui/Tree`** (on top of `Ui/RowSurface`):

- **`TreeSurface`**: indentation, expand toggles, indent guides, selection, find highlights, sticky
  ancestor headers, keyboard navigation (up/down, left collapses or goes to parent, right expands,
  page, home/end), hit testing and automation peers. Driven by an `ITreeRowCursor`.
- **Row painting by styled runs.** The format turns a row into runs of `(text, style class)` -
  JSON's key, punctuation and typed value; XML's tag, attribute name, attribute value and text -
  and the surface owns layout, caching, clipping at the display cap and the palette. That makes
  syntax colouring a shared feature, which is the XML roadmap's last item for free.
- **Collapsed summaries** supplied by the format (`{ 12 keys }`, `<item> … </item>`) through the
  same run interface.
- **Gutters as providers.** The surface hosts any number of gutter columns; JSON's schema gutter is
  one provider, not surface code.
- **Breadcrumb bar** fed by the format naming each ancestor segment, and a generic
  `ISearchNavigator` adapter that seeks the cursor to a match offset.

**Where XML differs**, so the generic types leave room for it without anything XML being built:

- Close tags are real rows, so `Close` is a first-class shape, not a JSON afterthought.
- Mixed content: text, comments, CDATA and processing instructions are `Leaf` rows interleaved with
  elements, and checkpoints may land before any of them.
- Resuming at a checkpoint needs the namespace bindings in scope. The ancestor chain gives each
  enclosing element's start offset, so the XML cursor re-reads those start tags - one short read per
  ancestor. The generic cursor contract has to allow a format-specific resume step for this.
- Attributes: inline in the element's row, with a long attribute list clipped by the display cap
  like a long JSON string. Whether they can also expand as child rows is an XML-view decision the
  run interface does not constrain.

**Keeping the abstraction honest.** With JSON the only real format, the generic types would drift
JSON-shaped. The tests include a minimal test-only tree format - an S-expression-style scanner and
cursor with mixed leaf and container children and explicit close rows - driven through
`SparseContainerIndex` and `TreeSurface`. It stands in for XML until XML exists.

## Consumers

| Consumer | Today | With the sparse index |
|---|---|---|
| `JsonVisibleRowCollection` | materialised `visibleRows`, `Rebuild` per toggle | replaced by the surface and `JsonRowCursor` |
| `JsonRowFactory`, hints, schema resolution | per token index | per cursor row; schema node inherited down the cursor's stack |
| `JsonPathBuilder` | parent chain of token indices | cursor stack, or container chain plus a parse of at most `T` |
| `JsonPathResolver` | token walk | checkpoint ordinals for array steps; key scan skipping values via the structural mask for object steps |
| `JsonOffsetTokenResolver`, `JsonSearchNavigator` | offset to token by binary search | offset to container chain, then parse at most `T` to the row |
| `JsonArrayElementIndex`, `JsonArrayRowCollection` | token walk | the container's checkpoints are the element index |
| `JsonArrayColumnDiscovery`, `JsonDocumentKeySampler` | token walk | sample a spread of checkpoint spans |
| `DateHintInference` | token walk | sample, as above |
| `JsonDiffIndex`, `JsonDiffRowCollection`, `JsonContentHasher` | 8-byte Merkle hash per token | see Diff below |
| NDJSON's hosted `JsonViewModel` | small per-line index | unchanged in shape; a line is usually below `T`, so no index at all |

## Diff

Diff is the one consumer that wants per-node data: aligning the children of a large array of small
elements wants a hash per child. It gets its own budget rather than blocking the rest:

- Hashes recorded only for large containers and for each checkpoint span; small subtrees hashed on
  demand while aligning, which reads at most `T` bytes each.
- If alignment inside a large array needs per-child hashes, they go in a separate log spilled to a
  memory-mapped temp file for the diff session's lifetime, not into the shared index.

The existing dense index stays available to diff until this is built, so diff is migrated last.

## Order of work

Each step lands on its own and leaves the app working. Tick a step when it is merged into the
branch, and note under it anything the next step needs to know.

Status: step 9 done; step 10 (remove the token index) next. The array table and the diff await a manual check. Branch: `plan/json-sparse-index`.

1. [x] **Benchmarks first.** A BenchmarkDotNet suite over three shapes - a token-dense array, deeply
   nested objects, a large array of small records - measuring index bytes per file byte, build time,
   time to first row, seek latency and backward-page latency. Run against the current index to fix
   the baseline.

   `JsonTreeIndexBuildBenchmarks` and `JsonTreeNavigationBenchmarks` over `JsonShapeCorpus` (64 MiB,
   compact). Run: `dotnet run -c Release --project Argonaut.Tests -- --filter "*JsonTree*" --join`.
   Baseline for the dense index, Apple M5, .NET 10:

   | Shape | Index / file | Full build | First screen | Seek to middle | Page back |
   |---|---|---|---|---|---|
   | TokenDenseArray | 6.17x | 295 ms (217 MB/s) | 1.0 ms | 8.6 ms, not reached | 1.9 us |
   | DeepNesting | 4.01x | 226 ms (283 MB/s) | 1.3 ms | 2.1 ms, not reached | 2.1 us |
   | RecordArray | 2.74x | 150 ms (426 MB/s) | 1.3 ms | 7.3 ms, not reached | 2.4 us |

   - Seek resolves the token but the tree cannot show it: the middle of a large root array sits past
     `MaxDisplayedChildrenPerContainer`, so `FindVisiblePosition` returns null on every shape. The
     sparse surface has to *reach* the target, not only match the time.
   - Page back is cheap because the rows are materialised - but "fully expanded" is capped at
     10,002 / 690,002 / 140,002 rows of the 17M / 11M / 7.7M tokens.
   - Build throughput is the other headline: 217-426 MB/s is 10-20 s for a 4 GB file. The
     structural scanner is expected to beat it by several times.
2. [x] **Structural scanner** in `Features/Json/Indexing`, with no Avalonia reference: stage-1 masks,
   subtree skip, and a validating mode. Tested against `Utf8JsonReader` on a corpus including
   escapes, surrogates, strings containing brackets and quotes across chunk boundaries, and
   `GrowingByteSource`.

   `JsonStructuralScanner`: `Classify` (per-64-byte masks, `Vector128`, carry-state across blocks)
   and `TrySkipValue` returning `JsonSkipOutcome` (`Skipped` / `Incomplete` / `NeedsFullParse`).
   Tested against `Utf8JsonReader` on seeded random documents over whole, 37-byte-split and
   64-byte-split sources (`Support/SplitByteSource`), and on `GrowingByteSource`.
   `JsonStructuralScannerBenchmarks`, whole 64 MiB document: 12.4 / 13.9 / 14.1 ms
   (4.5-5.4 GB/s) against `Utf8JsonReader.Skip`'s 135 / 148 / 106 ms - 8-11x, no allocation.

   Decisions for the next steps:
   - **No validating mode in the scanner.** Validation stays with `Utf8JsonReader`, as a
     skip-only pass on its own background thread alongside the sparse build: it holds no memory,
     keeps today's error messages and `DescribeFailure` offsets, and at ~0.5 GB/s on another core
     does not hold up the build. A second, validating SIMD parser would be simdjson stage 2 for no
     gain the tree can see.
   - **Comments hand over.** A slash outside a string ends the masked scan (`NeedsFullParse`).
     The sparse build (step 3) does the same: on the first comment it continues from the enclosing
     checkpoint with a `Utf8JsonReader`-driven event source, which is slower but emits the same
     events.
   - Step 3 adds a comma mask to `Classify` for child boundaries; a `:` mask is not needed, since
     a checkpoint goes after a comma and a key is re-read from there.
   - Escapes are walked a backslash at a time, not with simdjson's carry-add; revisit only if a
     backslash-heavy corpus profiles as hot.
3. [x] **`SparseContainerIndex`** in `Engine/Indexing/Trees`, fed by the JSON scanner as an
   `IBackgroundIndex`, alongside the existing index rather than replacing it. The test-only tree
   format drives it too, from the first commit.

   Design worked out before starting:
   - **Events.** A format feeds `Open(offset, formatKind)`, `Separator(resumeOffset)` (the
     previous child ended and another follows; a parse may resume here) and
     `Close(end, isEmpty)`. The first child is implied by `Open`. JSON defers each comma until
     the next significant byte, so a trailing comma (`[1,2,]`, allowed today) is not a separator;
     that needs a non-whitespace mask from `Classify`.
   - **Promotion, not close-time recording.** A container is recorded when the scan passes
     `start + T`, while still open - the root array of a multi-GB file is open until the last
     byte, and the view needs its checkpoints long before then. Ancestors cross before
     descendants, so records are appended in start order and a parent is always recorded before
     its children. `End` and `ChildCount` are written on close, publish-then-mutate with
     `Volatile`, the same pattern as `EndIndex` today.
   - **Record**: start, end, parent, depth, ordinal in parent, child count, format kind.
   - **One global checkpoint log** in offset order: `(offset, ordinal, container)`. Taken only
     in the innermost open container, only once it is recorded, at the first separator `B` bytes
     past that container's previous checkpoint.
   - **Resume at offset X**: the innermost recorded container C enclosing X (binary search on
     start, then up the parent chain); the greatest checkpoint at or before X; if that belongs to
     a descendant, walk up to C's direct child D and resume at `D.end` with ordinal
     `D.ordinalInParent + 1` - a large child's end is itself a resume point in C. If it belongs to
     an ancestor, resume at C's start.
   - **Resume at ordinal k in C**: over the checkpoints inside C, "ordinal in C" is monotonic
     (a descendant's checkpoint maps to its ancestor-in-C's ordinal), so binary search works at
     O(depth) per probe.
   - **Comments.** Rather than a `Utf8JsonReader` event source, a comment-aware scalar
     `Classify` producing the same masks (comment bytes read as whitespace, comment state in the
     carry). The first slash outside a string switches that block and the rest of the scan to it.
     `TrySkipValue` can use it too, and stop answering `NeedsFullParse` for comments.
   - **Sub-commits**: the generic index with the test-only format; the JSON event source over
     `Classify`; the comment-aware classifier; the validation pass; build benchmarks beside the
     dense ones.

   Built as designed: `Engine/Indexing/Trees` (`SparseContainerIndex`, `SparseContainerIndexBuilder`,
   `TreeContainer`, `TreeCheckpoint`, `TreeResumePoint`), `JsonBlockClassifier` (vector path, and
   a comment-aware scalar path it switches to for good at the first comment), `JsonSparseIndex`,
   `JsonDocumentValidator`, `JsonFailureLocation` (shared with the dense index). Tested through
   `Support/SExpressionTreeFormat` against a whole-document model, and for JSON against a
   `Utf8JsonReader` model with and without comments and trailing commas, over whole, split and
   still-arriving sources.

   Learned on the way:
   - **Checkpoint eligibility is by position** (past `start + T`), not by whether the container
     is recorded yet: promotion also happens at `Advance`, whose timing follows the source's buffer
     boundaries, and the same bytes must give the same index.
   - **A parent's checkpoint can sit exactly at a child container's start**, so "inside a
     container" means strictly after its start, in both resume searches.
   - **The validator gathers across piece boundaries** into a pooled buffer, since
     `Utf8JsonReader` cannot resume inside a token cut by one. The dense index still cannot read a
     split source; it never gets one today.
   - **`AllItemsPublished` waits for validation**, so a failure is always visible before it.
     `Structure.IsComplete` says the structure is done, which is sooner.

   Measured on the benchmark corpus (64 MiB):

   | Shape | Structure only | With validation | Dense build | Index / file |
   |---|---|---|---|---|
   | TokenDenseArray | 66 ms | 147 ms | 295 ms | 0.009x |
   | DeepNesting | 53 ms | 142 ms | 226 ms | 0.009x |
   | RecordArray | 40 ms | 111 ms | 150 ms | 0.009x |

   The 0.009x is almost all the two logs' first `SegmentedAppendLog` segments (~580 KB); the
   records themselves are a few KB. That fixed cost matters for small documents - the NDJSON pane
   indexes one line at a time - so size the first segment down or skip the index below `T` when
   step 7 wires it in.
4. [x] **`ITreeRowCursor` and `JsonRowCursor`**: forward and backward over display rows with expand
   state, cross-checked against a walk of the dense index on the same corpus - the dense index is
   the test oracle. The test-only format gets its cursor here. Run it over
   `Fixtures/unicode-names-and-values.json` too, and step 7's painter the same way
   (`JsonUnicodeRenderingTests` is the dense tree's version): names and values in many scripts,
   emoji sequences, combining marks, invisible characters and `\u` escapes.

   Built differently from the names above: the stepping logic is generic, so there is no
   `ITreeRowCursor` interface and no `JsonRowCursor`. `TreeCursor` (in `Engine/Indexing/Trees`,
   with `TreeRow`, `TreeNode` and `TreeExpandState`) walks any format through an
   `ITreeFormatReader`, which only finds the next child, a container's first-child position, a
   value's end and a close row's start. `JsonTreeReader` is JSON's; `SExpressionTreeFormat.Reader`
   is the test format's, and XML will be the third. Tested forward, backward and by seek against
   whole-document models for both formats with random expansion, on the Unicode fixture, and row
   for row against `JsonVisibleRowCollection`.

   - **Backward** finds the previous sibling from the nearest resume point and skips siblings
     whole; it never walks rows. The cursor keeps the last 1,024 siblings a walk passed, which took
     paging back through a flat array of numbers from 8.3 ms to 9 us a screen.
   - **Row identity** is `TreeRow.Key`: the value's start, and whether it is the close row.
   - **Still to handle when wiring the view**: a container still arriving reports its end as
     `long.MaxValue` from `JsonTreeReader.SkipValue`, so rows after it are not reachable until
     it closes.

   `JsonTreeNavigationBenchmarks`, 64 MiB, fully expanded, seek then a screen of 50 rows / page
   back 50 rows:

   | Shape | Sparse seek | Dense seek | Sparse page back | Dense page back |
   |---|---|---|---|---|
   | TokenDenseArray | 364 us, reached | 8.6 ms, not reached | 9 us | 1.9 us |
   | DeepNesting | 24 us, reached | 2.1 ms, not reached | 14 us | 2.1 us |
   | RecordArray | 15 us, reached | 7.3 ms, not reached | 67 us | 2.4 us |

   The dense page-back is a lookup into rows it already materialised (and capped); the sparse one
   reads bytes. Both are far inside a frame. Sparse seek on a flat array is dominated by reading up
   to 64 KB of tiny siblings one at a time from the checkpoint; counting commas over classifier
   masks instead would cut it if it ever matters.
5. [x] **`Ui/RowSurface`**: extract the shared parts of `RawTextSurface`, with the raw view unchanged in
   behaviour.

   `Ui/Rows/RowSurface` - `Ui/Rows` rather than `Ui/RowSurface`, so the namespace and the class
   do not share a name. It holds the fixed row height, the appearance properties, the background
   fill that makes the surface hit-testable, horizontal pan with the widest-row high-water mark,
   and the `ILogicalScrollable` plumbing; a derived surface supplies `ExtentHeight` and
   `OnOffsetChanged`. `RawTextSurface` derives from it with no change in behaviour; the raw view's
   351 tests pass unchanged.

   Deliberately left in `RawTextSurface`: the caret, its blink and text selection (the tree
   selects rows, it has no text caret), and the line-number and wrap gutters (the tree's gutters
   are providers, built in step 6). Move them down if a second surface ever needs them.

   Needs a manual check, since no test draws pixels: the raw view's scrolling, horizontal pan,
   selection, caret and find highlights look and behave as before.
6. [x] **`Ui/Tree/TreeSurface`** with styled-run painting, gutter providers, keyboard navigation,
   selection and accessibility, exercised in tests through the test-only format.

   `TreeSurface` on `RowSurface`, fed a `TreeDocument` (index, reader, `ITreeRowPainter`,
   `TreeExpandState`, `ITreeGutter`s, available length, a `Grew` event). Rows are the painter's
   `TreeRun`s laid out as one `TextLayout` with per-run brushes (`TreeRunStyle`, a format-neutral
   vocabulary, mapped to brushes by `RunBrushes`). `TreeSurfaceAutomationPeer` reports a tree named
   by the selected row. `TreeCursor.Clone` added so the surface walks without moving its anchor.
   Tested headless over the S-expression format: realized rows are the viewport's, small scrolls
   move by rows, a jump lands at the same fraction of the file, keyboard selection (arrows, page,
   home/end, left/right collapse, expand and climb), reveal with ancestor expansion, the arrow click,
   and the layout cache staying at a viewport while paging the whole document.

   - **Scroll model**: position = the anchor's byte position as a fraction of the document. The
     surface is not in a `ScrollViewer`: sharing one offset with a host, and re-syncing it after
     every scroll, made a dragged thumb stutter and scrolling up from the end snap back. The view
     owns a plain `ScrollBar` and tells input apart by `ScrollEventType`: a dragged thumb calls
     `ScrollToFraction` (the bottom of the track calls `ScrollToEnd`), arrows and track pages call
     `ScrollByPixels`, as do the wheel and trackpad in the surface itself. The bar only follows
     `ScrollPositionChanged`, and not at all while its thumb is held.
   - **Not built**: sticky ancestor headers, indent guides (deferred with the rest of the chrome
     polish), text selection and copy within a row. Worth adding with step 7 or after.
   - **Needs eyes once JSON is on it** (step 7): row drawing, arrows, selection colour,
     highlights, gutters and the scrollbar thumb's behaviour while dragging.
7. [x] **JSON on the tree surface**: a JSON row painter, the schema gutter provider and hints, replacing
   the tree `ListBox`.

   Replaced in place, no toggle. `JsonViewModel` runs `IndexedSourceSession<JsonSparseIndex>` and
   exposes a `TreeDocument`; `JsonView` is a `TreeSurface`, a pan scrollbar and the path bar.
   `Features/Json/Tree` holds `JsonTreeText` (row text, child counts, value ends),
   `JsonTreePainter`, `JsonSchemaResolver` (top-down, cached per container), `JsonSchemaGutter`
   (resizable, tooltips), `JsonTreePaths` and the `JsonRowLink`s. The surface gained link runs,
   row markers, gutter tooltips and resizing, Alt deep-toggle within a row budget, and
   `TreeDocument.Close` so a surface lets go before the mapping is released. `TreeRow` carries
   its parent's start; `TreeCursor.Ancestors` yields rows. The array index moved from before the
   arrow to a marker slot in the same place; indent guides and hover highlight are not drawn.
   `JsonViewTests` drives the view headless; everything else is checked row for row against the
   dense tree (`JsonTreePainterTests`, `JsonTreePathsTests`).

   Needs a manual check: rows, colours, arrows, the array-index marker, selection, the schema
   gutter (drag its edge, hover for the tooltip), date-hint and "view as table" links, Alt-click,
   right-click copy, the path bar, find highlights and the pan and vertical scrollbars.
8. [x] **Consumers** moved one at a time per the table above: paths and search, then array table and
   samplers.

   Done: paths (`JsonTreePaths`), search (`JsonSearchNavigator`, `NdJsonSearchNavigator` reveal an
   offset), the schema root-key sampler and date-scheme inference (sparse overloads), hint
   overrides keyed by value offset, NDJSON's nested per-line tree.

   The array table: `JsonArrayTableSession` is one sparse session over the array's range;
   `JsonArrayElements` replaces `JsonArrayElementIndex` (no background walk - element `i` comes
   from `SparseContainerIndex.FindResumePoint`, and while scanning the count is
   `KnownChildCount`, the ordinal at the array's latest checkpoint); rows, discovery and cell
   routes read children through `JsonTreeReader`/`JsonTreeText`. The cell pane is a
   `TreeDocument` over the cell's own bytes on a `TreeSurface` - so its root row no longer shows
   the member name (the pane title does), and it scrolls by wheel and keys with no scrollbar
   until step 12's shared scroll host.

   Needs a manual check: open "view as table" on arrays of objects and of scalars, expand and
   collapse column headers, reshape, click cells (scalar and container) and use the pane's tree.
9. [x] **Diff** on its own hash budget.

   `JsonSparseIndex.StartIndexingWithContentHashes` feeds the validation pass's tokens to a
   `JsonContentHashRecorder`, which records the hash of every container of at least `T` (the
   promotion size) as it closes - no pass of its own, and a record set the size of the sparse
   index. `JsonContentHashes.Hash(start, end)` answers from the record or re-parses the value's
   bytes, at most `T` for a container. `JsonDiffIndex` records `JsonDiffNode`s (row start and
   value start) instead of token indices and reads children through `JsonDiffDocument`, which
   also builds the diff's `JsonRow`s, paths and find locations; the worker and the view hold one
   each. `JsonRow.TokenIndex` became `ValueStart`. The diff keeps its `ListBox` and record log -
   moving it to a tree surface is a view change, not an index one, and nothing here needs it.

   - **No per-child log.** The plan's spilled per-child hash log was not needed: alignment is
     already capped at `MaxAlignableArrayElements` per level, and the children of a level are
     hashed from their bytes as the level is aligned. A recorded array over the cap is now
     flagged approximate from its count, without reading it.
   - **Find keys a match by the deepest node holding it**, via a fully-expanded `TreeCursor`
     seek, remembering the last leaf found so a name and its value cost one seek. The lookup of
     the record covering a match now skips the end of a cross-parent move that does not draw
     that side; before, a match there could resolve to the wrong end and never be a stop.
   - The widest alignable array (`JsonDiffAlignmentBenchmarks`, 100K elements) takes 11-17 ms
     and allocates 23-25 MB, against the token index's 5-6 ms and 13 MB: the elements are read
     and hashed rather than looked up, and held as `TreeNode`s rather than token indices.
     What hashing adds to the validation pass is not measured yet.

   Needs a manual check: compare two files; changed paths open to the differing leaf; expand
   unchanged, added, removed and moved rows; the context bar's values and paths (including
   under a moved container); next/previous change; "changes only"; find across both files.
10. [ ] **Remove `JsonStructureIndex`'s dense log** once nothing reads it, and the `ChildCap`/"show more"
    machinery with it.
11. [ ] **Per-depth row counts**, if the estimated scrollbar proves not good enough in use.
12. [ ] **One scroll interface for every self-drawn view.** Move the tree's scroll API down into
    `RowSurface` - scroll by pixels, go to a fraction, go to the end, `ScrollPositionChanged`,
    `ShowsEnd` - and the scrollbar wiring now in `JsonView` into one reusable host in `Ui/Rows`,
    so the raw view, the JSON tree and the XML tree scroll through the same code and behave the
    same at the ends and under a dragged thumb. Two position models behind it: **exact** (row x
    height, for a view that knows its row count - the raw view, whose thumb stays row-accurate)
    and **estimated** (the top row's byte offset as a fraction of the document - the trees).
    The raw view then leaves its `ScrollViewer` too. Per-depth row counts (step 11) would give the
    tree an exact model under the same interface. The array table's cell pane gets a scrollbar
    from it too. Do before the XML view is built; the raw view's scrolling needs a manual check
    after it.
13. [ ] **The diff on the tree surface**, so every tree view works one way. Today the diff is a
    `ListBox` over a materialised list of visible rows: it holds no dense index, but it keeps
    what the old JSON list had besides - a list growing with what is expanded (nesting
    multiplies the 10K-per-container cap), a full re-walk on every toggle, and children past the
    cap unreachable. Instead:
    - **A merged-tree cursor** that steps through the record log and, inside an expanded
      undescended region, hands over to a `TreeCursor` on the side that draws it (left for
      removed and unchanged, right for added, the drawing end of a move), then back.
    - **A two-pane painter**: each row paints its left and right runs at the shared depth, with
      the status tint, the value-changed highlight, move badges and notes.
    - **A scroll model for the merged tree**, which has no one byte offset to take a fraction of
      - an estimate over the record log, and each expanded region's own byte fraction within
      its record. Needs step 12's interface first.
    - Selection, next/previous change, "changes only", find reveal and the context bar move to
      it; `JsonDiffRowCollection`, `JsonRow`, `JsonRowPresenter` and the display cap go.

## Later

- **JSONC comments as rows.** Settings files lean on them, and today `JsonTreeReader` skips them as
  trivia. They would be leaf rows in `TreeRunStyle.Comment`, which exists for this and for XML.
  Needs the reader to report a comment as a child (and ordinals to skip it, so array indices and
  paths are unchanged), and the sparse index's separator logic to treat a comment as trivia still.

## Open questions

- Default `T` and `B`, and whether they scale with file size or a memory budget. Settle from the
  step-1 benchmarks.
- Whether closing brackets remain rows. Today they are; the cursor can emit them either way.
- Key lookup in objects with millions of keys: a linear key scan is GB/s, but a path resolver that
  repeats it may want a sparse key-hash filter per large object.
- Whether `Vector512` is worth a separate path over `Vector256` on the hardware this runs on.
