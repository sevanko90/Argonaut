# Hex view

Goal: show any document's bytes as a hex dump - offset, hex bytes, and their characters - so a
binary response or file can be read rather than only saved or handed to another app. It is the
second step of binary support (#51). The first step is in place: `FileKind.Binary` and
`BinaryViewModel` (`Features/Binary`) describe a binary document and offer Save as… and Open in
default app.

Status: planned, nothing built.

## To decide

- **Does Hex replace the Binary view or sit beside it?** Recommended: replace it. A detected binary
  document opens in Hex, and the format line, media type, size, Save as… and Open in default app
  move into the hex view's toolbar. The switcher then offers Hex for every document, text included,
  which is useful in itself (finding a stray BOM, a NUL, a mis-encoded byte). Keeping both means two
  views for the same bytes, and one of them can only describe them.
- **Bytes per row.** Recommended: 16, fixed, which is what every hex tool shows and what makes
  offsets easy to read. Fitting the width (8/16/32) is a later option. A fixed width is also what
  makes the row geometry pure arithmetic.
- **Character column encoding.** Recommended: ASCII printable bytes, with `.` for the rest. A UTF-8
  column (a multi-byte character drawn across its bytes) is a later option.

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

For the byte at the caret, show it read as int8/16/32/64 (signed and unsigned, little- and
big-endian), float32/64, a UTF-8 character, and a Unix timestamp. It reads at most 8 bytes, so it
is cheap however large the document.

### Image preview (separate, later)

This is not part of the hex view. It is the image option from #51: a thumbnail in the binary
document's toolbar or as a view of its own, decoded with Avalonia's image support and capped by
size, because decoding is whole-image in memory.

## Steps

1. [ ] `HexRows` geometry and a read-only `HexSurface` + `HexViewModel` registered in
   `DocumentViewCatalog` (switcher option "Hex"); offset/hex/character columns, scrolling, the
   status line. Tests: geometry (`long` rows, last partial row), a `GrowingByteSource` row count,
   a `SplitByteSource` row straddling a boundary, and a headless scroll-then-`CaptureRenderedFrame`
   test (as `RowSurfaceTests` does).
2. [ ] The decision on Binary: if Hex replaces it, the toolbar takes over Binary's actions and
   detection's `FileKind.Binary` opens Hex. Tests: a detected binary document opens in Hex, and
   the toolbar's actions reach `IBinaryDocumentActions`.
3. [ ] Caret, selection, keyboard movement, copy (hex, characters, bytes); `IByteRangeNavigable`.
   Tests on the view model; a switch from Raw lands on the same bytes.
4. [ ] Go to offset.
5. [ ] `BytePatternMatcher` and the find bar's Hex mode. Tests: parsing (`0x` prefixes, spacing,
   odd digit counts rejected), matches across chunk boundaries (`ChunkOverlap`).
6. [ ] Value inspector.
7. [ ] Architecture doc, roadmap.

## Not in this plan

- Editing bytes (overwrite or insert). The raw view's piece table could carry an overwrite mode
  later, but saving binary edits has to keep the save rules in CLAUDE.md, which is its own plan.
- Structure templates (parsing a PNG's chunks, a ZIP's directory).
- Comparing two binary documents.
