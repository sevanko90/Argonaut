# An XML view

Goal: open an XML file of any size as a tree, the way the JSON view opens JSON - shown at once,
indexed underneath in the background, coloured, searchable, with the selected row's XPath in the
path bar and a box to go to one.

The tree machinery is already format-agnostic - `SparseContainerIndex`, `TreeCursor` over an
`ITreeFormatReader`, `TreeSurface` over an `ITreeRowSource`, painters as styled runs - and the
test-only S-expression format (`Argonaut.Tests/Support/SExpressionTreeFormat`) keeps it honest. An
XML view adds a scanner, a reader, a painter and path support, plus a view model and view on the
shared tree-view code.

Test file: `python3 scripts/make-test-json.py --xml` writes `~/testData/citygml-sample-500mb.xml`
(CityGML 2.0, 63,765 city objects, 5.9 million elements, eleven namespaces) and prints the XPaths
worth trying. It is never part of a bare run.

## Decided

- **XPaths as written.** The path bar spells a path with the document's own prefixes:
  `/core:CityModel/core:cityObjectMember[1234]/bldg:Building/bldg:boundedBy[3]`. Go-to matches
  names by their bytes as written, so it is lexical, not namespace-aware: two prefixes bound to
  the same URI are different names, and an element under a default namespace is its bare name
  (`/core:CityModel/core:cityObjectMember[49]/Building`). A namespace-correct "copy strict
  XPath" is a later option, not part of this plan.
- **Structural validation only.** The one indexing pass reports what breaks the tree: a close tag
  that does not match its start tag, elements still open at the end, a close tag with nothing to
  close, an unterminated tag, comment, CDATA section, processing instruction or DOCTYPE, a second
  root element, and non-whitespace text outside the root. Namespace declarations, entity
  definitions, encoding and DTD rules are not checked: a viewer, not a validator. Like JSON, the
  scan carries on past an error so the rest of the file stays reachable, and the first error is
  the one reported, with line and column.
- **Whitespace-only text is not a row**, as whitespace between JSON values is not. The Raw view
  shows the exact bytes. Text with any visible character is a row, trimmed for display.
- **Detection by content.** After an optional UTF-8 BOM and whitespace: `<?xml`, or a `<`
  followed by a name character, `!--`, `!DOCTYPE` or `?`. HTML stays out (see Design).
- **Read as UTF-8.** Display decodes UTF-8, as every other view does. A file declaring another
  encoding opens with a status note saying it is shown as UTF-8; UTF-16 (by its BOM) is not
  detected as XML at all and opens in Raw.
- **Attributes sit inline** in their element's row, clipped by the display cap. Attributes as
  child rows are a later option.
- **Look by screenshots.** Row colours and the collapsed summary are chosen by the user from
  screenshots of variants (step 7), not decided in the code steps.

## Design

### Shared tree-view code moves down first

JSON is the only tree view today, so code an XML view would otherwise copy lives in
`Features/Json`. Two features sharing it means it moves down (CLAUDE.md), before XML is built on
it:

- **The view model base.** Everything in `JsonViewModel` that is about showing a tree rather
  than about JSON - the `TreeDocument`, expand state and default depth, indent guides, selection
  (`SelectedRow`, `SelectedPath`, `SelectedPathSegments`, `SelectedValueText`), reveal and pending
  reveal, `RevealMatch`, `HighlightTerm`, `CurrentMatchIn`, the growth monitor, `CloseTree` and
  `ShowSelectionInText` - moves to an abstract `TreeDocumentViewModel` in `Ui/Tree`, deriving from
  `IndexedDocumentViewModel`. Format-specific parts are abstract or overridable: building the
  reader, painter and gutters, path segments and resolution, value text.
- **The path segment.** `JsonTreePathSegment` becomes `TreePathSegment(string Label, long Target)`
  in `Ui/Tree`.
- **The path bar.** The bar in `JsonView.axaml` (segments, entry box, go-to, copy value, copy path,
  Ctrl/Cmd+L) becomes a `TreePathBar` control in `Ui/Tree`, told its icon, placeholder
  (`$.path.to[0]` / `/root/child[2]`) and tooltips ("Go to a JSONPath" / "Go to an XPath").
- **The search navigator.** `JsonSearchNavigator` becomes `TreeSearchNavigator` in `Ui/Tree`, over
  the base view model.
- **The toolbar's tree controls.** The expand-depth button and the indent-guides toggle in
  `JsonToolbarView` become one control in `Ui/Tree` that both toolbars host.

It is a refactor: no behaviour changes, and the JSON tests pin it. The rule of thumb for what
moves: anything the XML step would otherwise copy verbatim.

### Detection (`Engine/Detection`)

`FileTypeDetector.FileKind.Xml`, appended after `Tsv`. Checked before the CSV/TSV rule, on a
prefix of at most 64 KB:

- Skip a UTF-8 BOM (`EF BB BF`) and XML whitespace (space, tab, CR, LF). A UTF-16 BOM
  (`FF FE` / `FE FF`) is not XML here.
- `<?xml` followed by whitespace is XML.
- Otherwise the first byte must be `<`, followed by a name-start byte (ASCII letter, `_`, `:` or
  any byte ≥ 0x80), `!--`, `!DOCTYPE` or `?`.
- **HTML stays out.** A `<!DOCTYPE html` (any case) is not XML. Otherwise walk past leading
  comments, processing instructions and a DOCTYPE to the first start tag: a name of `html` in any
  case is not XML. XHTML with an XML declaration is XML, since `<?xml` decides first.

`IsPlausibleFor(Xml)`: the first byte after the BOM and whitespace is `<`.

### Node kinds (`Features/Xml/Indexing/XmlNodeKind`)

The `TreeNode.FormatKind` byte:

| Kind | What | Container |
|---|---|---|
| `Document` (255) | the document level | - |
| `Element` | an element whose content holds an element, comment or processing instruction | yes |
| `TextElement` | an element whose content is only text and CDATA: `<gml:name>Foo</gml:name>` | no |
| `EmptyElement` | `<a/>`, `<a></a>`, or content of whitespace only | no |
| `Text` | a text run with a non-whitespace character, in mixed content or at an element's edges | no |
| `CData` | `<![CDATA[...]]>` among other children | no |
| `Comment` | `<!--...-->` | no |
| `ProcessingInstruction` | `<?target data?>` | no |
| `XmlDeclaration` | `<?xml ...?>` at the very start | no |
| `Doctype` | `<!DOCTYPE ...>`, internal subset included | no |

**A text-only element is one row**, `<gml:name>Foo</gml:name>`, not three. This is what makes XML
readable as a tree, and the commonest element in most files.

**The scanner and the reader must agree on which elements are containers**: exactly those whose
content holds an element, a comment or a processing instruction. The index records containers
and the cursor trusts it, so a 20 MB `gml:posList` recorded as a container by one and read as a
leaf by the other would break seeking. A test checks the agreement directly (step 4).

Row identity (`TreeNode.ValueStart`, which is also `RowStart`): the `<` of any markup node; the
first non-whitespace byte of a text run. `ValueEnd`: one past the node's last byte - past the
close tag's `>` for a `TextElement`; for `Text`, the next `<`.

### The scanner and index (`Features/Xml/Indexing`)

**`XmlStructuralScanner`**: the span-level primitives both the index and the reader use, all
over `IByteSource`, none allocating:

- `TagEnd(start)`: one past a start or end tag's `>`, quote-aware (`IndexOfAny('>', '"', '\'')`
  per step); self-closing when the byte before `>` is `/`.
- `CommentEnd`, `CDataEnd`, `ProcessingInstructionEnd`: `IndexOf` of `-->`, `]]>`, `?>`.
- `DoctypeEnd`: quote-aware, tracking the `[...]` internal subset, ending at a `>` outside it.
- `NextMarkup(at)`: `IndexOf('<')` - the vectorised skip through text.
- `TrySkipElement(start, out end)`: past an element's matching close by depth counting, with the
  same outcomes as `JsonStructuralScanner.TrySkipValue` (skipped / incomplete / not followable).

Every primitive respects the `IByteSource` contract: it advances by the length returned, treats
an empty span as the end, and handles a construct split across spans (`SplitByteSource` tests
cover it). None snapshots `AvailableLength`.

**`XmlSparseIndex`** (`IBackgroundIndex`), patterned on `JsonSparseIndex`: the same promotion and
checkpoint sizes (64 KB each, smaller in tests), `SparseContainerIndexBuilder` events, the same
growth handling (re-read `AvailableLength` every turn, `WaitForLength` while unsettled, nothing
finalised before `LengthSettled`), `ReadAhead`, `Reopen` and `DetachStructure` with an
`XmlKeptStructure`, and progress every 4 MB. One pass, one background task: there is no second
validator.

The scan is a resumable state machine fed span by span - text, inside a tag (and which quote),
inside a comment, CDATA, processing instruction or DOCTYPE - so nothing it reads is ever
required whole. It turns markup into builder events:

- **A child starts** (a start tag, a non-whitespace text run, a comment, CDATA, a processing
  instruction, a DOCTYPE): `Separator(childStart)` unless it is its parent's first child. For a
  text run, `childStart` is its first non-whitespace byte.
- **A start tag is held, not opened.** Whether the element is a container is known only at the
  next markup inside it. If that is its own close tag (text and CDATA in between don't decide it),
  it was a `TextElement` and nothing is opened. If it is a child element, comment or processing
  instruction, `Open(tagStart, Element)` is emitted then, before the child's events. While an
  element is held, `Advance` is capped at its start, so the published scan position never passes
  a container the index has not opened.
- **A close tag** is compared with the open element's name, by bytes: the start tag's name
  offset and length are kept on a stack, and the close tag's name read with `RequireContiguous`
  (names over 4 KB are a failure). A mismatch records the failure, then recovers the way a
  reader of the file would: if an element further up is open under that name, everything inside
  it closes at the close tag, and so does it; if none is, the close tag is skipped. So one stray
  or missing tag does not nest the rest of the file a level deeper. Then `Close(end, isEmpty)`.
- **At the end**, open elements close where the data ends and the scan fails, as JSON's does.

Failures are `IndexFailure`s carrying offset, line and column. Line and column are worked out only
when a failure is recorded, by counting newlines up to it (`Count` is vectorised).

**Same-name counts at checkpoints.** An XPath step is `name[k]`: the k-th sibling with that name,
not the k-th child. Counting siblings from a container's start on every selection would read up
to the whole container - 500 MB for a CityModel member near the end. So the scan keeps, per open
element, a small table of child-name hash (`XxHash3` of the name's bytes as written) to count,
with text, comments and processing instructions under reserved keys. When a `Separator` adds a
checkpoint (the builder's checkpoint count grows), the index stores a snapshot of that table
beside it; when a recorded container closes, it stores its totals. A container with more than 16
distinct child names stores no snapshots and is counted by reading - such containers are rarely
wide. Cost: one hash per element name, and a few dozen bytes per 64 KB checkpoint in typical
files (under 4 MB per GB at the cap).

### The reader and row text (`Features/Xml/Tree`)

**`XmlTreeReader`** (`ITreeFormatReader`), patterned on `JsonTreeReader`:

- `TryReadChild` skips whitespace-only text; at `</` the container closes (`closeStart` is the
  `<`). A start tag is read to its end; if it is not self-closing, the reader looks ahead to the
  next markup, through text and CDATA, to classify it as `Element`, `TextElement` or
  `EmptyElement` - the same rule as the scanner. The look-ahead is one `IndexOf('<')` per piece of
  markup, which over a 20 MB text node is one vectorised pass, as `JsonTreeReader.StringEnd` is
  over a long string.
- `FirstChildPosition`: just past the start tag's `>`.
- `SkipValue`: `XmlStructuralScanner.TrySkipElement`, with JSON's mapping of outcomes.
- `CloseStart`: the last `<` before the container's end.

No namespace state: as-written paths need none, so resuming at a checkpoint needs nothing from the
ancestors' start tags.

**`XmlTreeText`**: decoded on demand and capped at `DisplayText.MaxLength`, like `JsonTreeText` -
an element's qualified name, its attributes as (name, value) spans, text content trimmed, and a
container's child count for its summary (the same capped, cached count JSON's summary uses).
Text keeps its entity and character references as written, as JSON shows escapes, so search
highlights line up with the bytes. A row is one line, so a run of whitespace holding a line
break or tab - between attributes, inside a comment, in text - shows as one space; text is
trimmed, and an element of only whitespace shows as `<name></name>`. Copy value keeps the bytes
as written.

**`XmlTreePainter`** (`ITreeRowPainter`): runs per node kind, in the existing `TreeRunStyle`s -
`Punctuation` for `<`, `</`, `>`, `/>`, `=` and quotes; `Name` for element names;
`AttributeName`, `AttributeValue`; `String` for text and CDATA content; `Comment` for comments
and the DOCTYPE; processing instructions as `Keyword` target and `Plain` data. A close row is
`</name>`. A collapsed container is its start tag, a muted child count, and `</name>`. A
truncated text gets the size note and "Open in raw" action JSON's long strings get. No marker.
`XmlTreePalette` maps the styles to theme brushes. The proposals here are defaults until step 7.

### XPaths (`Features/Xml/Tree/XmlTreePaths`, `Features/Xml/Paths/XmlPathSyntax`)

**Segments**, built from the cursor's ancestry as `JsonTreePaths.Segments` is: the root is
`/name`; each step below is `name[k]`, `text()[k]`, `comment()[k]` or
`processing-instruction('target')[k]`, with `[k]` 1-based among siblings of the same name or
kind. `[1]` is left out when the sibling is the only one of its name - known exactly for a small
container (counted by reading it, at most the promotion size) and for a recorded container once
it has closed (its totals); while a recorded container is still being indexed, `[k]` is always
shown. A document has one root element, so the root's step is never numbered; other top-level
nodes are numbered until the index is complete. CDATA and text are numbered together as `text()`, as XPath does, though touching ones
are separate rows here. The XML declaration and a DOCTYPE are not XPath nodes: their path is
`/`, which Go-to resolves to the document's first row.

`k` for a row in a recorded container: the snapshot at the nearest checkpoint at or before the
row, plus a read of at most 64 KB of siblings from there. Selection stays a cheap UI-thread
operation, as JSON's is.

**Go-to** (`XmlPathSyntax`): absolute paths of `/`-separated steps, each a QName, `*`, `text()`,
`comment()` or `processing-instruction(...)`, with an optional integer predicate. Anything else
(`//`, axes, other predicates, relative paths) is refused with a toast naming what is supported.
Resolution walks down one container at a time on the pool, as `JsonTreePaths.Resolve` does; in a
recorded container it binary-searches the checkpoints' snapshots for the k-th name and reads at
most 64 KB from there.

### Search

No new search. `SearchSession` already scans bytes, and the tree reveals a match by its offset:
`TreeSearchNavigator` (moved down in step 1) over the XML view model. A match in an attribute
lands on its element's row; in the text of a `TextElement`, on that row. As in JSON, search is
byte-literal: `&amp;` in the file is not found by searching `&`.

### View model, view and shell

- **`XmlViewModel`** (`Features/Xml`) on `TreeDocumentViewModel`: loads a whole origin or a range,
  starts `XmlSparseIndex` through `IndexedSourceSession`, keeps the structure through
  `IndexBasis` like JSON, builds reader, text and painter, and supplies paths and the go-to.
  Copy value copies a text row's or a `TextElement`'s text as written; for a container, its
  start tag.
- **`XmlViewSettings`**: expand depth and indent guides, settable properties (the init-only
  trap in CLAUDE.md).
- **`XmlView`**: `TreeSurface` and `TreePathBar`; link clicks (Open in raw) as JSON's.
- **`XmlToolbarView` / `XmlToolbarViewModel`**: the shared expand-depth and indent-guide control.
  Value-hint settings join it in step 8.
- **Shell**: `DocumentViewCatalog` registration and display order (JSON, NDJSON, XML, CSV, TSV,
  Raw text); `MainWindow.axaml` data templates for the view and its toolbar; `--view xml` in
  `StartupArguments`, the `--help` text and `CommandLineDialog`; a selection of lines (`--filter`)
  turned down for XML as for JSON, with the same `--view xml` error; the README's views list. The
  diff stays JSON-only.

## Steps

Each step is written test-first: the failing tests land first and are checked to fail for the
right reason, then the implementation, then the full suite (run in the background). Steps 1-6 and
8-9 are well pinned by their tests and suit hand-off; step 7 is a look decision and stays with
the main thread and the user.

0. [x] Test file: `make-test-json.py --xml` (CityGML 2.0, ~500 MB), checked well-formed with
       expat's namespace-aware parser.
1. [x] **Move the shared tree-view code down** (Design, first section). Tests: the existing JSON
       view, view model, search and disposal tests pass unchanged; `ArchitectureTests` passes. No
       new behaviour, so no new tests beyond moving any that test the moved types.
2. [x] **Detection.** Tests in `FileTypeDetectorTests`: `<?xml` with and without a BOM, a bare root,
       a leading comment, a non-HTML DOCTYPE, a processing instruction first; `<!DOCTYPE html>`,
       `<!doctype html>`, `<html>`, `<HTML lang="en">` after a comment are not XML; a UTF-16 BOM is
       not XML; JSON, NDJSON, CSV, TSV and INI detection unchanged; `IsPlausibleFor(Xml)`.
3. [x] **Scanner and index.** A `RandomXml` generator in `Tests/Support` (elements, attributes with
       both quotes and `>` inside values, text, mixed content, CDATA holding `<` and `]]`, comments,
       processing instructions, a DOCTYPE with an internal subset, namespaces, self-closing and
       empty elements, whitespace-only runs, multi-byte UTF-8). Tests, at small promotion and
       checkpoint sizes:
       - The recorded containers and checkpoints equal a naive model of the same document (parsed
         with `XmlReader`, whitespace-only text dropped, text-only and empty elements as leaves), as
         the S-expression and JSON index tests do.
       - The same over `SplitByteSource` split at every offset, and over `GrowingByteSource` fed in
         small pieces: identical result, and nothing finalised before the length settles.
       - A text-only element far past the promotion size (a 200 KB text at promotion 1 KB) is never
         recorded.
       - Each failure in Decided, with its line and column; the scan carries on and completes.
       - Same-name snapshots equal naive counts at every checkpoint; a container with 17 distinct
         child names stores none.
       - `Reopen` / `DetachStructure` as `JsonKeptStructureTests` does.
4. [x] **Reader, text and painter.** Tests:
       - A `TreeCursor` walk over `XmlTreeReader` gives the naive model's rows (shape, depth, ordinal,
         kind) for random documents, fully expanded and at default depths; stepping backward gives the
         same rows reversed.
       - `SeekTo` every offset lands on the row containing it.
       - **The scanner and the reader agree on containers** for every element of the random documents.
       - Painter runs for every node kind; a collapsed summary; a 20 MB text is capped with its note and
         Open in raw; attribute values holding `>` and both quote styles.
5. [x] **View model, view, toolbar and shell.** Tests: the catalog maps `Xml`; a headless load of a
       sample document shows the expected rows (as `JsonViewTests`); a search match reveals its row;
       the disposal and lifecycle tests (`DocumentDisposalLifecycleTests`, `CollectionDisposedEmptyTests`)
       include the XML view; `--view xml` parses and `--view xml --filter` is refused; a non-UTF-8
       encoding declaration shows its status note.
6. [x] **XPaths.** Tests: segments for known rows of a fixture (default namespace, `text()`,
       `comment()`, `processing-instruction()`, `[1]` left out for a unique name, kept while indexing);
       same-name `k` across checkpoints equals a naive count; for every row of random documents,
       resolving its formatted path returns that row; each unsupported syntax gives its message.
7. [ ] **Look** (main thread with the user): screenshots of the CityGML sample in two or three
       variants per open choice - attribute colours, `xmlns` declarations muted or plain, prefixes
       muted or not, the collapsed summary - published side by side for the user to pick from. Then
       the palette and painter are set to the picks.
8. [ ] **Value hints on text.** Text rows and `TextElement`s get the JSON tree's value hints (dates,
       URLs, colours, JWTs, ...) through `ValueHints.Read`, by shape as always. The generic link
       records (`OpenUrlLink`, `ViewInRawLink`, `DateSchemeLink`, `ExpandHintLink`) move down to
       `Ui/Tree` for both views; the hint settings join the XML toolbar. Tests: a date hint on
       `core:creationDate`, a URL on `core:informationSystem` that opens through `OpenAddress`,
       a colour on `gen:value` `#8B4513`.
9. [ ] **Benchmark.** An `Xml` document kind in `IndexMemoryBaseline` (a CityGML-like generator in
       the harness, so no file is kept), run at the usual size, and a section added to
       `index-benchmarks.md`. Target: at least JSON's speed (about 500 MiB/s on the M5), allocation
       independent of the file's size, index kept within about 1.5 MB per GB.
10. [ ] **Docs.** `architecture.md` gains the XML view; the roadmap's XML entry is replaced by what
       remains (below).

## Not in this plan

Kept for the roadmap once this lands: namespace-correct "copy strict XPath", namespace URIs on
hover, attributes as child rows, XSD-labelled rows (the JSON schema gutter's counterpart),
decoding non-UTF-8 encodings, XML colouring in the Raw view, XML in the diff, and editing.
