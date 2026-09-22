# Editing inside a very long line — options

Decision record for the one case the raw editor is still slow at, written up because the analysis
that picks between the options is more work than the options are to describe, and two earlier
attempts at a shortcut were both wrong.

**Status (2026-09-22):** decided and built — a variant of
[option C](#c--make-a-rows-boundaries-independent-of-its-content) that keeps the UTF-8 backoff but
measures it from the line's arithmetic cap, together with spans that are runs of whole lines. See
[long-line-edit-plan.md](long-line-edit-plan.md) for where it departs from C and why; the analysis
below is kept as the reasoning that led there. Status as first written (2026-09-21): open, leaning
towards C.

## The problem, precisely

Typing inside a soft-wrapped line costs about 40ms per keystroke once the line is around 100MB.
Everywhere else in the same document it is instant, including *later in the same line* and
*immediately after it*.

`RawEditedRowIndex` re-derives the rows an edit disturbed and stops when it can prove the edited
and original byte streams have rejoined: offsets differing by exactly the document's byte delta,
and agreement on whether a line starts there. Past the last edit the bytes are then identical, so
a row's extent — which depends only on the bytes from its start — cannot diverge again.

Inside one line that proof does not arrive until the line's real newline. The walk is about 30ns
a row, so it scales with the line: 1.2ms at 1MB, 2.1ms at 8MB, 16ms at 32MB, ~40ms at 105MB.

The cost is bounded by the *line*, never by the file, and everything else about editing a 4GB
document is already instant. So this is a real but narrow problem, and the question is whether it
is worth a change to the row model to remove it.

## Why the two obvious bounds do not work

Both were written into [roadmap.md](roadmap.md) as the fix, and both are wrong. The measurement
that settles it is `RawLongLineReflowTests`, and its result is the opposite of what it looks like:

- Over **ASCII**, an insert leaves every later break inside the line at the **same absolute
  offset** — 200 of 200 in the measurement. A forced break is pure arithmetic from the row start
  when nothing backs off, so the boundaries do not move; the bytes at them do.
- Over **multi-byte** content, `RawRowBoundary.BreakAtCap` backs off up to 3 bytes to avoid
  splitting a character, so the breaks follow the characters to **old offset plus the byte
  delta** — 194 of 200.

**A — converge at zero displacement.** Since ASCII boundaries do not move, declare convergence
when the two streams sit at the same offset with no delta applied. Unsound: the bytes being read
there are *not* the bytes the original index was built over, they are those bytes shifted, so
nothing beyond the next boundary is proven. One different backoff moves the next row, and that
chains.

**B — chain capped spans.** Cap a span at some number of rows; when it fills, start another that
begins where it ended; only the last converges back to the original index. Re-derive just the
member an edit lands in, and check that its end did not move to decide whether the rest of the
chain survives. The same hole one level down: the check says nothing about the *next* member,
whose content has also shifted. The assumption moves, it does not disappear.

Both are recorded because they are the ideas a reader has first, and both look right until the
measurement above.

## The options

### A — accept the walk

Do nothing here. The walk is bounded by the line, the rest of the document is unaffected, and the
**re-index over the piece table** already on [roadmap.md](roadmap.md) dissolves the span when it
eventually lands: a rebuild re-scans the edited document, so the long line is freshly anchored and
there is no dirty span left over it at all.

- **For:** no work, no risk, and the case is narrow — a document with 100MB lines that someone
  wants to edit *at the top of* one.
- **Against:** the re-index is itself unbuilt and is sequenced with saving, so "later" is a long
  way off; and 40ms a keystroke is unpleasant rather than merely slow.

### B — re-derive on idle rather than per keystroke

Orthogonal to the others and much the cheapest. Apply the edit to the piece table immediately
(microseconds) and defer the row index's re-derivation until a pause in typing, coalescing a burst
of keystrokes into one walk.

- **For:** small, contained, and it removes the *symptom* for every long-line case at once.
- **Against:** it needs a defensible answer for what the row index reports while a walk is
  outstanding. `RowCount` drives the scrollbar extent and is read every frame; rows past the edit
  are not merely stale but wrong. Serving them from the pre-edit derivation is a lie that shows up
  as rows drawn at the wrong offsets; blocking on the walk when one is asked for puts the cost back
  on scrolling instead of typing. Probably: keep the previous row count as provisional, let the
  viewport near the caret be served from the last anchor before the dirty point (which is
  correct), and force the walk for anything that reads past it.
- **Note:** this hides the cost rather than removing it, and it stacks with either A or C.

### C — make a row's boundaries independent of its content

The only option that removes the walk rather than hiding it.

Today a forced break lands at `WrapWidth` bytes from the row start, then backs off up to 3 bytes
so a multi-byte character is not split — and that backoff is what makes a boundary depend on the
bytes at it. Instead, put the break at exactly `WrapWidth` from the row start and let the row
*above* draw a straddling character whole, reading up to 3 bytes past its own range; the row below
begins drawing after it.

Boundaries inside a line then become pure arithmetic, and the consequences fall out:

- An insert of N bytes inside a line **moves no later boundary in that line at all**. Rows between
  the edit and the line's end keep their exact extents — what changes is only the bytes drawn in
  them, which is the display's business and not the index's.
- The line's row count becomes `ceil(lineLength / WrapWidth)`, so the rows it gains or loses is
  arithmetic rather than something to be walked to.
- The dirty region collapses to the *end* of the line: plus or minus a row there, and the constant
  byte and row shift for everything after it. Finding that end needs no scan either — anchors
  carry line numbers in ascending order, so a binary search over them plus a bounded walk lands on
  the row where the line ends.
- The editing cost inside a long line becomes the same as anywhere else.

It also makes the background scan marginally faster, since `BreakAtCap`'s backoff loop disappears,
and it changes nothing for binary content, which already breaks at the cap.

**What it costs**: a row's byte range stops being the same thing as the bytes it draws. That is a
change to what `RawRowInfo` and `RawDecodedRow` mean, and it is the reason this needs analysing
rather than just doing.

## What C still needs settling

The call sites that would have to agree on the new meaning, all under `Argonaut/Features/Raw`:

- `RawRowBoundary` — the rule itself. Keep the newline cases and the end-of-data case exactly as
  they are; only `BreakAtCap` changes, and it gets simpler.
- `RawRowDecoder` / `RawDecodedRow` — the substantive one. A row currently decodes `[start, end)`
  and reports `DisplayByteLength`. It would need to decode from *before* its start (up to 3 bytes,
  for a character the row above already drew) or to know that its first whole character begins
  partway in — which is a second offset alongside `DisplayByteLength`, not a replacement for it.
- `RawCaretStops` — legal caret positions come from the decoded row, so they follow the decoder.
  The question that needs an answer first: for a character straddling a boundary, is the caret
  stop *after* it inside the row above (past its range) or the row below (inside its range)?
  `CaretAffinity` already exists for a boundary belonging to two rows and is the obvious place to
  settle it.
- `RawSegmentIndex`, `RawEditedRowIndex` — no structural change, but every anchor offset a scan
  produces differs from today's. Nothing is persisted, so there is no migration, only re-indexing.
- `RawTextSurface` — `DrawSelection` and `OffsetAt` both map between byte offsets and the drawn
  text through `RawDecodedRow`, so they follow it. Selection painted across a straddling character
  is the case to look at by eye.
- `RawWordStops`, `RawCaretReadout` — read through the same decoder; expected to follow for free,
  worth confirming.
- `RawTextExtractor` — copies raw bytes for a range and is unaffected.

Open questions, in the order they need answering:

1. **Which row draws a straddling character, and does the other one leave a gap?** Drawing it
   whole in the row above makes that row up to one glyph wider than the column, which for a wide
   emoji is visible. The alternative — U+FFFD on both sides — is simpler, loses information, and
   looks worse in exactly the corrupted-file case the raw viewer exists for.
2. **Where does the caret sit either side of it?** See `RawCaretStops` above.
3. **Does anything actually regress for a reader?** The viewer is read-only for most users, and
   this changes what they see at a wrap boundary in non-ASCII content. Worth looking at a real CJK
   or emoji-heavy file before committing.
4. **How many existing tests assert specific boundaries?** `RawSegmentIndexTests`,
   `RawRowDecoderTests` and `RawCaretStopsTests` all pin byte offsets that would move. The oracle
   tests in `RawEditedRowIndexTests` compare against a fresh index and so survive untouched, which
   is the useful half of the suite here — but the expected values elsewhere are a real cost to
   re-establish, and re-establishing them by running the new code and pasting the output is how a
   suite stops being evidence.
5. **Is the arithmetic exactly right at a line's end?** `ceil(lineLength / WrapWidth)` has to agree
   with what the walk produces for the trailing partial row, the `\n`-exactly-at-the-cap case, and
   a file with no trailing newline. That is a property test against the existing walk, and it
   should be written *before* the change, since it is the thing that would silently be wrong.

## Related

- [editing-options.md](editing-options.md) — the editing design this sits inside; §"What typing
  became" has the measurements and the span model.
- [roadmap.md](roadmap.md) — where this sits against everything else deferred, including the
  re-index over the piece table that option A leans on.
- `RawLongLineReflowTests` — the measurement both dead ends foundered on.
