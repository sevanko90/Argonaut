# Hex view

Goal: show any document's bytes as a hex dump - offset, hex bytes, and their characters - so a
binary response or file can be read rather than only saved or handed to another app. It is the
second step of binary support (#51); the first (Binary, describing a binary document with Save as…
and Open in default app) shipped in v2.3.2 and is folded into the hex view by step 2.

Status: steps 1-4, 6, 8 and 9 built on branch `hex-view` - a read-only, polished hex view that binary documents open in.

## Decided

- **Hex replaces the Binary view.** A detected binary document opens in Hex, and the format line,
  media type, size, Save as… and Open in default app move into the hex view's toolbar. The switcher
  offers Hex for every document, text included, which is useful in itself (finding a stray BOM, a
  NUL, a mis-encoded byte). `BinaryViewModel` and its view go; the actions interface stays, as
  `IDocumentExportActions` in `Features/Hex`, with `DocumentExport` implementing it.
- **16 bytes per row, fixed.** It is what every hex tool shows, it keeps offsets easy to read, and
  it makes the row geometry pure arithmetic. Other widths (8/32) are a later option.
- **The data column always shows bytes, in file order.** A byte-size selector (1, 2, 4, 8) only
  groups them visually; it never turns them into numbers or reorders them. Interpreting bytes is
  the side panel's job. The row stays 16 bytes, which every group size divides.
- **A side panel replaces the value inspector.** It sits right of the dump, a fixed width, and
  has tabs: **Values** reads the caret's group as the types its size can hold, in one byte order
  chosen by a switch, and **Struct** is the structure decoder (docs/struct-decoder-plan.md). The
  two are not used at the same time, so they share the space rather than splitting it.
- **The character column reads ASCII or UTF-8**, chosen from the toolbar and remembered. ASCII:
  printable bytes (0x20-0x7E) as themselves, `.` for everything else. UTF-8: see below.

## Design

### Geometry (`Features/Hex`)

`HexRows` is pure arithmetic: row `r` starts at `r × 16`, the row count is
`ceil(AvailableLength / 16)`, and the byte at offset `o` is on row `o / 16`, column `o % 16`. There
is **no index and no background scan**: a multi-GB document opens as fast as a small one, and
`IndexingTask` is complete from the start.

- **Row numbers are `long`.** A 40 GB file has 2.7 billion rows, which is past `int`. Check that
  `RowSurface`'s scroll model (pixels as `double`, as `RawTextSurface` uses it) stays exact at that
  size. A double holds integers exactly up to 2^53, so 2.7 billion rows of about 18 px is far
  inside that.
- **A growing source** (a streamed origin) has a row count that grows. Re-read `LengthSettled` and
  then `AvailableLength` on every layout, and treat the last row as partial until the length is
  settled (see CLAUDE.md on `AvailableLength`). `GrowingByteSource` is the test.

### Reading

Each visible row reads its 16 bytes with `IByteSource.CopyTo` into a `stackalloc` buffer. That
covers a range split across buffers for free, so the view needs no `RequireContiguous` and
allocates nothing per row. The view opens its own source from the origin, and its session releases
it (the cancel → join → release ordering, although no scan runs).

### Surface

`HexSurface : RowSurface` draws three columns per row: the offset (hex, as many digits as the
document's length needs, fixed when the document opens so no measurement changes while drawing),
the 16 bytes in two groups of 8, and the character column.

- Draw with `RenderRows` only. Anything that changes another control goes through
  `OutsideRenderPass` (CLAUDE.md). The widest row is known arithmetically, so the pan scrollbar
  never needs to be resized while drawing.
- Format into a pooled `char` buffer per frame, never a `string` per byte. Follow
  `RawTextSurface`'s text-layout caching for the glyphs.
- Colour, using the raw view's palette: zero bytes muted, printable ASCII normal, control and
  high bytes in a third shade. Colouring by byte class is what makes structure stand out in a dump.

### Selection, caret and copy

- A byte caret and a selection, kept in step between the hex and character columns, as two
  boundaries between bytes (`HexCaret`): with nothing selected the caret is on a byte, and a
  selection runs to the document's end at most. Clicking in either panel sets both and makes
  that panel the one the keys and a copy act in (Tab swaps it); a drag or Shift-click selects
  from byte to byte, both included. Arrow keys, Page Up/Down and Home/End move by byte, row and
  screen, Cmd/Ctrl+Home/End to the ends, Shift extends. The selection is shaded behind both
  panels. The caret is the accent's solid block with the byte redrawn on it in the active panel
  (readable over any byte colour), blinking to its outline while the view has focus, and the
  outline alone in the other panel. The pointer's mark is softer - a pale accent block with a
  faint edge, behind the text - so the caret stays the focus. The character panel spaces its
  characters 2 px wider than the font, so a mark clears its neighbours.
- Cmd/Ctrl+C copies as the active panel shows it - hex text (`89 50 4E 47`) or the character
  column - and the context menu offers both and the bytes themselves, which go on the clipboard
  as binary data (`public.data` on macOS, `application/octet-stream` elsewhere); pasting them
  into the app opens them again byte for byte. A copy over 16 MB is refused, not truncated.
- The status strip shows where the caret is, as the raw view's does: the caret's position
  (`Byte 1,234    0x000004D2`: decimal, then the hex offset as the gutter shows it) at the
  right, in one fixed place, with the selection length beside it when something is selected. It follows the caret as it moves by
  click or key. What the byte *is* - hex, decimal, bits, ASCII name - is not on the strip; the
  side panel's Values tab shows that, and more (step 6b).
- Pointer tracking stays, as a mode beside caret tracking: the byte under the pointer keeps its
  highlight in both panels. It keeps working while the mouse drags out a selection for highlight
  and copy - the drag sets the selection, the pointer highlight carries on over it. The strip
  follows the caret, not the pointer.
- `IByteRangeNavigable`: Hex reports its selection as a `ByteRange` and reveals one. Switching
  from Raw or JSON to Hex therefore lands on the same bytes, and "show in hex" works from the other
  views for free.

### Go to offset

Clicking the caret's position in the status strip, or Ctrl/Cmd+L (the tree views' go-to key; the
shell's new request bubbles, so a view's entry claims the key first), swaps the position for
an entry holding the caret's offset in hex, all selected. It takes decimal (`8000`), hex (`0x1F40`,
`1F40h`, or any digits with a hex letter), `+`/`-` steps from the caret, and ignores spaces, `_`
and `,` (`HexOffsetEntry`). Enter puts the caret on that byte, selecting nothing, a third of the
way down the screen; an entry that is no offset, or one past the end, says why in a toast and stays
open. It is pure arithmetic, with no waiting. (Ctrl/Cmd+G stays find next.)

### Find

**To be specced before step 5 is built.** What is here is a sketch. Searching a hex view is
mostly searching for bytes, not text, and that needs more thought than a second mode on the
find bar.

The sketch: `SearchSession` already scans bytes in chunks through an `ISearchMatcher`, so hex
search is a new matcher, not a new scan. `BytePatternMatcher` takes a byte sequence parsed from
the find term (`89 50 4E 47`, spaces optional) and matches exactly. In the hex view the find bar
offers "Text" and "Hex" modes, Text using `LiteralSearchMatcher` unchanged. `ISearchNavigator`
reveals a match as a selected byte range.

Open questions for the spec:

- **How the kind of search is chosen.** A Text/Hex switch, or read from the term (`0x` prefix,
  pairs of hex digits). Which one the hex view starts in, and whether it is remembered.
- **What a byte pattern can say.** Exact bytes only, or also wildcards (`89 ?? 4E`) and nibble
  masks. Whether the term can be pasted straight from Copy as hex.
- **Text in a binary file.** UTF-8 only, or also UTF-16 (little- and big-endian), which is how
  Windows binaries hold their strings. Whether ASCII case-insensitivity applies.
- **Searching for a value.** A number as the bytes some type would hold (`1234` as a little-endian
  uint32, a float) - the Values tab's readings (step 6b) run backwards. Possibly its own step.
- **Starting points.** Find from a selection (select bytes, Ctrl/Cmd+F fills the term), from the
  caret forwards, and wrapping; whether a selection can limit the search to itself.
- **Showing matches.** Every match highlighted in both panels and the current one marked, beside
  the caret, the selection and the pointer's mark; a match running across rows; the count and
  where the scan has reached, as the find bar shows it today.
- **Cost.** Matches can be dense in binary data (a search for `00`), so how many are kept and
  drawn has to have a limit, as the text views' search does.

### Byte grouping

A byte-size selector in the toolbar - 1, 2, 4 or 8 - sets how the data column groups its bytes.
It is display only: the side panel does not depend on it.

- Groups sit at offsets that are multiples of their size from the start of the document, so a
  group's place is still arithmetic (`o / size`). Reading at an unaligned offset is what the
  panel is for.
- A group is its bytes drawn together with a space after it: `01020304 05060708` at size 4,
  `0102030405060708` at 8. Inside a group the bytes stand 2 px apart, so each byte's digits stay
  readable without a full space; at 8 that adds 28 px to a row, which is still narrower than an
  ungrouped one. The bytes stay in file order and keep their byte-class colours. Each byte's
  digits are laid out once per value (256 at most) and placed by arithmetic, so a row draws no
  text of its own for its hex.
- Every size has a fixed cell width, so a row's width is known before anything is drawn, the pan
  scrollbar is sized when the size changes (outside the render pass), and the header labels each
  group by its offset (`00 04 08 0C`). The dashed line between the two halves of a row stays.
- The caret, the selection and the pointer highlight cover a whole group in the data column and
  its bytes in the character column - the caret its own byte there, while the keys move by byte
  in that panel. A click, drag or shift-click in the data column takes whole groups. Arrow keys
  move by group in the data column and by byte in the character column. A short last group is
  drawn and marked as far as the document goes.
- The size is remembered (`HexViewSettings`, key `hex`), for every hex view.

### Side panel

A panel right of the character panel, filling the space the grid leaves, shown or hidden from
the toolbar. Its width does not depend on what it shows, so the grid never shifts. Two tabs; the
chosen one is remembered as a setting.

#### Values

Reads a selection of one to eight bytes - any length, 3 included - or, with nothing selected (or
more than eight), the caret's group. What is read is what the dump marks, so its source is never
in doubt. A `-  24-bit  +` stepper in the header changes the width a byte at a time by selecting
that many bytes from where the reading starts, so a wider read from a one-byte grouping is a click.
Showing every size at once was tried and dropped: most of its readings were noise, and nothing in
the dump showed which bytes they came from.

| Width | Readings |
|-------|----------|
| 1 byte | uint8, int8, ASCII name (`NUL`, `LF`), the UTF-8 character starting here |
| 2 bytes | uint16, int16, `Half`, UTF-8 text |
| 3, 5, 6, 7 bytes | uintN, intN (`uint24`), UTF-8 text |
| 4 bytes | uint32, int32, `float`, Unix seconds, UTF-8 text |
| 8 bytes | uint64, int64, `double`, Unix seconds, Unix milliseconds, UTF-8 text |

- **One byte order at a time**: LE / BE chips in the panel's header, starting at this machine's
  order and remembered (`HexViewSettings.ValuesByteOrder`). Once an order suits a file it rarely
  changes, so the other does not need the room beside it. A reset icon beside them goes back to
  this machine's order, its tooltip naming which that is, and is greyed out when it is in use.
- The header says what is read and where: "Values", the width with its stepper, the order, and
  the offset. Below it, the bytes read, in file order.
- **Bits** shows the bytes read as 0/1 digits grouped by byte (`0100 1001`), four to a line.
- Hovering a reading marks the bytes it covers in the dump (a UTF-8 character can run past a
  one-byte group); clicking it copies its value.
- Bytes cut short by the end of the document read as `-`; a lone byte is always whole.
- The panel is 340 px wide whatever it shows, so the dump never shifts; it is shown or hidden
  from the toolbar, and that is remembered.

It reads at most 8 bytes with `CopyTo` into a `stackalloc` buffer and decodes with
`BinaryPrimitives`, so it costs nothing however large the document. The decoding sits in
`Features/Hex/Values/ByteReading` because the structure decoder uses the same code, so a struct
field and the Values tab never disagree.

#### Struct

The structure decoder, planned in docs/struct-decoder-plan.md and built as its own step (7). Until
then the panel has the Values tab only.

### A UTF-8 character column

The hard part is a character whose bytes cross a row boundary: its lead byte ends one row and its
continuation bytes start the next, so no single row can draw it whole. One rule keeps every cell
mapped to exactly one byte:

- The character is drawn once, at its lead byte's cell. Within a row it may spread over its own
  continuation cells (a wide CJK glyph or an emoji takes the room its 3-4 bytes give it).
- When it crosses the row end, it is clipped at the row's last cell, and a continuation mark
  shows it carries on.
- Continuation bytes at the start of the next row show a muted continuation mark, not `.`, so they
  read as part of the character above, not as unknown bytes.
- To tell a row's leading continuation bytes apart, a row looks back at most 3 bytes before it
  starts. That keeps the geometry arithmetic and needs no scan.

Invalid sequences show `.` as the ASCII column does. Measure this against real files before it is
built: if clipped glyphs read badly, the alternative is to draw a crossing character on the row
where it ends instead.

### Image preview (separate, later)

This is not part of the hex view. It is the image option from #51: a thumbnail in the binary
document's toolbar or as a view of its own, decoded with Avalonia's image support and capped by
size, because decoding is whole-image in memory.

## Steps

1. [x] `HexRows` geometry and a read-only `HexSurface` + `HexViewModel` registered in
   `DocumentViewCatalog` (switcher option "Hex"); offset/hex/character columns, scrolling, the
   status line. Tests: geometry (`long` rows, last partial row), a `GrowingByteSource` row count,
   a `SplitByteSource` row straddling a boundary, and a headless scroll-then-`CaptureRenderedFrame`
   test (as `RowSurfaceTests` does).
   Also `--view hex` on the command line. Still to add: a growing document's new rows show on
   the next scroll or redraw, but nothing redraws as it grows - it needs a growth tick like
   `IndexGrowthMonitor` gives the indexed views.
2. [x] Hex replaces Binary: the toolbar takes over Binary's description and actions, detection's
   binary documents open in Hex, and `BinaryViewModel` and its view are removed. Tests: a detected
   binary document opens in Hex, and the toolbar's actions reach `IDocumentExportActions`.
3. [x] Caret, selection, keyboard movement, copy (hex, characters, bytes); `IByteRangeNavigable`.
   Pointer tracking kept as a mode (see Selection, caret and copy), not replaced by the caret,
   and working during a mouse drag-select. Tests on the view model; a switch from Raw lands on
   the same bytes.
   The status strip becomes the raw view's: it drops the byte's hex, decimal, bits and name (the
   Values tab brings them back in 6b) and shows the caret's position in decimal and hex, and the
   selection length,
   in a fixed place at the right. Tests: the position follows caret movement by key and click.
   Built: `HexCursor` (boundaries, moves, viewport, pending reveal), `HexCopy`, the surface's
   click/drag/shift-click, keys and copy menu; Copy bytes puts binary data on the clipboard
   (`public.data` / `application/octet-stream`), which paste-to-open reads first. Caret and
   pointer look chosen from screenshots in both themes.
4. [x] Go to offset. Tests: parsing, the view model's go-to and its messages, the entry's keys,
   and Ctrl/Cmd+L reaching the window only when no view claims it.
5. [ ] Find - **spec first** (see Find's open questions), then build. The sketch: a
   `BytePatternMatcher` and the find bar's Hex mode. Tests: parsing (`0x` prefixes, spacing,
   odd digit counts rejected), matches across chunk boundaries (`ChunkOverlap`).
6. [ ] Grouping and the side panel, in two parts:
   - a. [x] The byte-size selector grouping the data column: cell widths, the header, caret,
     selection and pointer highlight by group. Tests: geometry at each size, a partial last
     group, a growing document's last row. Look chosen from screenshots (2 px inside groups).
   - b. [x] `ByteReading` and the side panel with its Values tab: a 1-8 byte selection or the
     caret's group, any width, stepped from the panel, one byte order at a time. Tests: each reading in each order, a short last group, the panel
     following the caret and the grouping, the order and the panel remembered.
7. [ ] The structure decoder and the side panel's Struct tab - a large feature of its own, with
   its own steps in docs/struct-decoder-plan.md. It needs step 3's selection and 6b's
   `ByteReading` and panel.
8. [x] UTF-8 character column (`HexUtf8`), an ASCII / UTF-8 choice in the toolbar. Each cell is
   drawn from a glyph cache; a character crossing the row's end is clipped there and its bytes on
   the next row show a muted `·`. Copy as text follows the column. The status strip names the
   character the caret's byte is in, on the left as the raw view's does ("U+00E9 LATIN SMALL
   LETTER E WITH ACUTE"), whichever way the column reads. Tests: a character crossing a row,
   invalid and truncated sequences, the 3-byte look-back at a row start. Being tried on real
   files before it is called done.
9. [x] Visual polish, chosen from screenshots in both themes:
   - Three panels: a pinned offset gutter and the character panel on the panel tint, the bytes on
     the window background; the character panel and the header end where the data does.
   - A pinned header - "Offset", each byte's column, "Text" over the characters (it will hold
     more than ASCII one day) - set apart by its height, a line and a gap before the rows.
   - A faint dashed line between the two groups of eight.
   - The byte under the pointer: a mark in both panels over a faint band along its row. (Zebra
     striping and column bands were tried and dropped for this.) Step 3 gave its solid accent
     block to the caret and the pointer a softer mark.
   - Zero bytes fainter than the offsets, so they stand clear of black text in light theme.
   - A status strip, as the raw view's: the byte's hex, decimal, bits and ASCII name, and its
     position, following the pointer. Step 3 reduces it to the caret's position.
10. [ ] Architecture doc, roadmap.

## Not in this plan

- Editing bytes (overwrite or insert). The raw view's piece table could carry an overwrite mode
  later, but saving binary edits has to keep the save rules in CLAUDE.md, which is its own plan.
- Structure templates (parsing a PNG's chunks, a ZIP's directory). Mapping a pasted C/C++ or C#
  struct onto selected bytes is its own plan, docs/struct-decoder-plan.md, built on this one.
- Comparing two binary documents.
