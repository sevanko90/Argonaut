# Hex view

Goal: show any document's bytes as a hex dump - offset, hex bytes, and their characters - so a
binary response or file can be read rather than only saved or handed to another app. It is the
second step of binary support (#51); the first (Binary, describing a binary document with Save as…
and Open in default app) shipped in v2.3.2 and is folded into the hex view by step 2.

Status: steps 1-2 built on branch `hex-view` - a read-only hex view that binary documents open in.

## Decided

- **Hex replaces the Binary view.** A detected binary document opens in Hex, and the format line,
  media type, size, Save as… and Open in default app move into the hex view's toolbar. The switcher
  offers Hex for every document, text included, which is useful in itself (finding a stray BOM, a
  NUL, a mis-encoded byte). `BinaryViewModel` and its view go; the actions interface stays, as
  `IDocumentExportActions` in `Features/Hex`, with `DocumentExport` implementing it.
- **16 bytes per row, fixed.** It is what every hex tool shows, it keeps offsets easy to read, and
  it makes the row geometry pure arithmetic. Other widths (8/32) are a later option.
- **The character column is ASCII**: printable bytes (0x20-0x7E) as themselves, `.` for everything
  else. A UTF-8 column is a later option, held back by characters that cross a row boundary
  (below).

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

- A byte caret and a selection, kept in step between the hex and character columns. Clicking in
  either sets both, and arrow keys, Page Up/Down and Home/End move by byte, row and screen.
- Copy as hex text (`89 50 4E 47`), as the character column, or as raw bytes to the clipboard.
- The status line shows the caret offset (hex and decimal) and the selection length.
- `IByteRangeNavigable`: Hex reports its selection as a `ByteRange` and reveals one. Switching
  from Raw or JSON to Hex therefore lands on the same bytes, and "show in hex" works from the other
  views for free.

### Go to offset

Ctrl/Cmd+G takes an offset in hex (`0x1F40`) or decimal and reveals it. It is pure arithmetic,
with no waiting.

### Find

`SearchSession` already scans bytes in chunks through an `ISearchMatcher`, so hex search is a new
matcher, not a new scan: `BytePatternMatcher` takes a byte sequence parsed from the find term
(`89 50 4E 47`, spaces optional) and matches exactly. In the hex view the find bar offers "Text"
and "Hex" modes. Text mode uses `LiteralSearchMatcher` unchanged. `ISearchNavigator` reveals a
match as a selected byte range.

### Value inspector (later step)

For the bytes at the caret, show them read as int8/16/32/64 (signed and unsigned, little- and
big-endian), float32/64, a UTF-8 character, and a Unix timestamp. It reads at most 8 bytes, so it
is cheap however large the document.

It also offers **bits** as a view option: the same 1, 2, 4 or 8 bytes as 0/1 digits, grouped by
byte (`0100 1001`) and following the inspector's endianness switch. That makes flags and packed
fields readable without converting by hand.

### A UTF-8 character column (later option)

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
3. [ ] Caret, selection, keyboard movement, copy (hex, characters, bytes); `IByteRangeNavigable`.
   Tests on the view model; a switch from Raw lands on the same bytes.
4. [ ] Go to offset.
5. [ ] `BytePatternMatcher` and the find bar's Hex mode. Tests: parsing (`0x` prefixes, spacing,
   odd digit counts rejected), matches across chunk boundaries (`ChunkOverlap`).
6. [ ] Value inspector, with the bits option. Tests: each reading at both endiannesses, reads
   near the end of the document (fewer than 8 bytes left).
7. [ ] UTF-8 character column, if measuring says the boundary rule reads well. Tests: a character
   crossing a row, invalid and truncated sequences, the 3-byte look-back at a row start.
8. [ ] Architecture doc, roadmap.

## Not in this plan

- Editing bytes (overwrite or insert). The raw view's piece table could carry an overwrite mode
  later, but saving binary edits has to keep the save rules in CLAUDE.md, which is its own plan.
- Structure templates (parsing a PNG's chunks, a ZIP's directory).
- Comparing two binary documents.
