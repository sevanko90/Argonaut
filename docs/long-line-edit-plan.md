# Editing inside a very long line at constant cost — implementation plan

**Status (2026-09-22):** built. Departures from the plan as written: the two line queries live on
`RawSegmentIndex` only (internal) rather than on `IRawRowIndex` - the edited index answers its own
pre-edit line questions from its runs, so nothing needed them on the interface - and
`LineEndContaining` also returns the line number, which the rebuilt run's line accounting needs.
`RawEditedRowIndex` no longer takes the original bytes, which it had only used for the
convergence walk. The cursor's factory is `RawRowCursor.StartOfLine`, since C# will not let a
static method share the `AtLineStart` field's name. Supersedes the leaning recorded in
[long-line-reflow-options.md](long-line-reflow-options.md); that document's analysis is kept, and
§"Checks against the options document" below says where this plan departs from it and why.

Written for whoever implements it. Every design decision is settled here; what is left open is
called out as such.

## The problem

Typing early in a ~100MB soft-wrapped line costs ~40ms per keystroke. Everywhere else in a 4GB
document is instant, including later in the same line and after it. The cause is in
`RawEditedRowIndex`: it re-derives rows from the edit forward until it can *prove* the edited and
original byte streams have rejoined (offsets differing by the total byte delta, agreement on
whether a line starts there), and inside a single line that proof can only arrive at the line's
newline. The walk costs ~30ns a row, so it scales with the line.

The realistic pathological case for this application is worse than the one measured: a 4GB
minified JSON document is *one* line. Any design where an edit costs something proportional to the
line is not a fix for this app.

## Checks against the options document

Two findings that change the plan.

**Option C's "no structural change to `RawEditedRowIndex`" is wrong.** Its convergence test is
`originalStart + totalDelta == currentStart`. Under C, the forced breaks inside the edited line sit
at exactly the *same absolute offsets* as the original's, because the line start did not move — so
the two streams differ by 0, never by the delta, and the walk still runs to the newline. Removing
the walk requires the span model to change, not only the boundary rule. And a span that stores one
anchor per 64 rows, as today, still costs 780K anchors (12.5MB) for a single edit in a 4GB
single-line file, which blows `MaxHeldAnchors` on the first keystroke.

**Option C's display cost is avoidable.** C puts the break exactly at the cap and lets a multi-byte
character straddle it, which changes what `RawDecodedRow` means and reaches `RawRowDecoder`,
`RawCaretStops`, `RawTextSurface`, `RawWordStops`, `RawRowReader` and their pinned tests (the
document's open questions 1–4). None of that is needed. Keep the backoff, but **measure it from the
line's arithmetic cap rather than from the previous row's end**: caps sit at `lineStart + k·W`; the
break at cap `c` is `c − backoff(bytes at c)`; the next cap is `c + W` regardless of the backoff.
Each boundary then depends only on the ≤4 bytes at its own cap. An insert of δ bytes moves later
boundaries by at most 3 each and never chains; a line's row count is `ceil(len / W)`; any row of a
line is O(1) to locate. Rows are W±3 bytes instead of W−3..W, which is invisible — rows are already
ragged in glyph count. No decoder, caret or surface change.

## Outcome

- The boundary rule becomes *cap-anchored*, so a line's rows are arithmetic from its start.
- A dirty span becomes a **run of whole lines**, one 12-byte record per line, with rows computed
  rather than walked. One edit in a 4GB single-line file costs one record.
- An edit costs O(bytes inserted + lines touched) plus one or two bounded anchor-bucket walks in the
  original index — independent of line length and of file size.

## Does this regress ordinary editing?

It was asked, because an earlier cut that started spans at line starts blew the budget after a few
edit sites and was slower. That cut *walked and stored every row* from the line start to the
convergence point — 676,661 rows and 21MB for one edit in a 54MB line — and the walk was the cost.
The failure was per-row storage plus the walk, not the line-start origin. This plan never walks
inside a line and never stores a row: rows are arithmetic from the line record. That is only
possible because of the boundary-rule change, which is why the rule and the span model are one
change and not two. Without the rule, line-start spans regress exactly as remembered.

Case by case against today's anchor-based spans:

| | Today (anchor spans) | This plan (line runs) |
|---|---|---|
| First edit in a new place, short line | span from the anchor before it, walk ≤64 rows + a few, ~2µs | 1–2 bucket walks in the original to find the line's start and end, ~2–6µs |
| Next keystroke in the same place | resume from the last undisturbed anchor, re-walk up to 64 rows, ~2µs | edit lies inside a run: no original queries, scan the inserted bytes, touch one record, ~100ns |
| Row lookup inside a span (rendering, caret) | walk ≤64 rows from the span's anchor, ≤2µs | binary search + ≤2 four-byte peeks, ~100ns |
| Memory per place | ~2 anchors, 32B | 1 record, 12B |
| Budget in places | 64K anchors ≈ 32K places | 512K records ≈ 512K places |
| Two edits in one 4GB line | widen/re-walk, budget blown | one run, one record |
| Paste of N lines | N/64 anchors, walked | N records (12·N bytes), no walk |

The one new hazard is a run holding many lines: a paste of a million lines, or editing consecutive
lines so adjacent runs merge into one. An edit near such a run's start shifts every later record
(O(lines) per keystroke), and merging copies lists. The plan bounds it: a run holds at most
`MaxLinesPerRun` (4096) lines, a region needing more is emitted as several adjacent runs, and
adjacent runs merge only when the result fits. Per-keystroke list work is then ≤4096 records
(~4µs). Today's `MaxRowsWorthWidening` is the same kind of guard for the same reason.

The original index (`RawSegmentIndex`) is unchanged in size and scan speed: anchors stay 16 bytes,
the per-row walk gains one comparison.

## Design

### 1. Boundary rule: cap-anchored backoff (`Argonaut/Features/Raw/RawRowBoundary.cs`)

A row starting at `start` with next cap `cap` ends at:

1. the first `\n` in `[start, cap)` → `(nl + 1, soft: false)`; next cap `nl + 1 + W`
2. else `cap >= AvailableLength` → `(length, false)` — end of data
3. else the byte at `cap` is `\n` → `(cap + 1, false)`; next cap `cap + 1 + W` (the existing
   peek-extension, so a CRLF straddling the cap cannot leave a lone linefeed row)
4. else `(cap − BackoffAt(cap), soft: true)`; next cap `cap + W`

`BackoffAt(cap)` is today's loop verbatim — 0..3 continuation bytes, and 0 when all four bytes
`cap−3..cap` are continuation bytes (binary junk breaks at the cap) — applied at the cap. Row
lengths lie in `[W−3, W+4]`. Keep the `wrapWidth >= MaxUtf8Backoff + 1` guard.

Replace the six copies of the walk loop (three in `RawSegmentIndex`, three in
`RawEditedRowIndex`) with one cursor:

```csharp
/// Position state at a row start: enough to derive every row after it.
internal struct RawRowCursor
{
    public long Start;
    public long NextCap;        // lineStart + k·W for the next forced break
    public bool AtLineStart;
    public int LineNumber;

    /// Ends the row at Start and moves to the next one.
    public (long End, bool SoftWrap) Advance(IByteSource source, int wrapWidth);

    public static RawRowCursor AtLineStart(long start, int lineNumber, int wrapWidth);
}
```

### 2. Line geometry (new `Argonaut/Features/Raw/RawLineRows.cs`)

Pure arithmetic over `(lineStart L, N, terminated, W)` where `N` is the offset of the line's `\n`,
or the data length when the line is unterminated. It must agree byte-for-byte with the cursor walk.

- `Count`: `N > L ? ceil((N − L) / W) : (terminated ? 1 : 0)`
  (caps `L + k·W` with `k ≥ 1` and `L + k·W < N` are the soft breaks; plus the last row; an empty
  terminated line is one row of `\n`; an empty unterminated line at EOF has no row)
- `Start(k)`: `k == 0 ? L : cap_k − BackoffAt(cap_k)` with `cap_k = L + k·W` — a ≤4-byte peek
- `Range(k)`: `[Start(k), k == last ? (terminated ? N + 1 : N) : Start(k + 1))`, soft iff `k != last`
- `RowContaining(offset)`: `k = (offset − L) / W`; `k + 1` if `k < last` and `offset >= Start(k + 1)`;
  clamped to `last`

**Write the property test first** (`RawLineRowsTests`): random content mixing ASCII, `é`, `日`,
emoji, CRLF, `\n` exactly on a cap, 0x80 junk, an unterminated tail and empty lines, at
W ∈ {8, 16, 40, 80}; for every line, the geometry's rows must equal the rows a `RawRowCursor` walk
produces, and `RowContaining` must agree with the walk for every offset. This is the options
document's open question 5 and the thing that would otherwise be silently wrong.

### 3. `RawSegmentIndex` (`Argonaut/Features/Raw/RawSegmentIndex.cs`)

- `RawRowAnchor.PackedOffset` gains the anchor row's backoff in bits 61–62 (bit 63 stays the
  continuation flag; offsets stay below 2^61). `AnchorAt` returns a `RawRowCursor` with
  `NextCap = start + backoff + W` (for a line-start anchor the backoff is 0). Anchor stays 16 bytes.
- `ProduceRows`, `GetRowInfo`, `LineContaining`, `RowForOffset` walk via the cursor.
- Two new queries, each O(log anchors + one ≤64-row bucket walk), added to `IRawRowIndex` so the
  edited index can ask them of itself as well as of the original:
  - `LineStartContaining(offset) → (long Start, int FirstRow, int LineNumber)`. Walk the bucket
    holding `offset`, remembering the last line start passed; if the line begins inside that
    bucket (every short line) that is the answer in one walk. Otherwise binary-search the anchors
    by `LineNumber` for the first anchor with `LineNumber >= ℓ`: if it is line `ℓ` and at a line
    start, that is it; else walk the bucket before it until line `ℓ` begins.
  - `LineEndContaining(offset) → (long End, bool Terminated, int RowsEnd)`. Continue the same
    bucket walk to the row that ends the line; if the bucket runs out first, binary-search for the
    first anchor with `LineNumber > ℓ` and walk the bucket before it (or the last bucket).
  - Both are defined at `offset == AvailableLength`: a phantom empty unterminated line with 0 rows
    (`FirstRow == RowsEnd == RowCount`) when the data ends in `\n` or is empty, else the last line.

### 4. `RawEditedRowIndex` rewritten: spans are runs of whole lines

Keep the class name and public surface (`RowCount`, `GetRowInfo`, `RowForOffset`,
`LineContaining`, `CanAbsorbEditAt`, `ApplyEdit`, `NeedsRebuild`, `DescribeSpans`), the ordered
and disjoint span list, the `*Before` running totals and `Renumber`, and the two binary searches
`SpanAtOrBefore`/`SpanAtOrBeforeRow`. Delete the two-stream convergence walk, `PositionOriginal`,
`EditReach`, `DirtyFrom`, `ResumeBucket`, `SpanAnchor`, `MaxRowsWorthWidening` and the
anchor-stride widen-or-open rule. Spans no longer begin at anchors; they begin at line starts and
end at line ends.

```
LineRun (private)
  OriginalStartOffset, OriginalStartRow, OriginalStartLine   // where it begins, original coordinates
  OriginalRowsEnd, OriginalLinesReplaced                      // what it replaced in the original
  LineStarts : List<long>   relative to StartOffset; [0] == 0
  RowPrefix  : List<int>    rows before line i; RowsHeld is the total
  Extent, Terminated        // relative exclusive end; whether the last line ends in '\n'
  ByteDelta / RowDelta / LineDelta and the three *Before totals   // unchanged in meaning
  RowDelta  = RowsHeld − (OriginalRowsEnd − OriginalStartRow)
  LineDelta = LineStarts.Count − OriginalLinesReplaced
  StartOffset = OriginalStartOffset + ByteDeltaBefore; StartRow = OriginalStartRow + RowDeltaBefore
```

`MaxLinesPerRun = 4096`. A region needing more lines is emitted as several adjacent runs.

**Lookups inside a run.** `GetRowInfo`: binary-search `RowPrefix` for the line, `k = row − prefix`,
`RawLineRows.Range(k)` over that line (`N` is the next line's start − 1, or `Extent − 1` /
`Extent` for the last line by `Terminated`); the line number is `OriginalStartLine +
LineDeltaBefore + i` on `k == 0`, null otherwise. `RowForOffset`: binary-search `LineStarts`, then
`RowContaining`. `LineContaining`: the line index plus the same base. Outside runs: exactly
today's displaced-original path.

**`ApplyEdit(extent)`**, with `a = Offset` and `r = a + BytesRemoved` in *pre-edit* coordinates.
The runs still describe the pre-edit document at this point and the original is immutable, so
pre-edit questions are answerable; the only bytes read from `document` are the inserted ones at
`[a, a + BytesInserted)`, which are valid post-edit.

1. Find the line containing `a` and the line containing `r`: from the run each lies in, or from the
   original via `LineStartContaining` / `LineEndContaining` on `offset − ByteDeltaThrough` of the
   run before. Runs consist of whole lines, so an offset in a gap lies in a line the original
   describes entirely; `a == r == document length` is the phantom-line case.
2. Region = `[lineStart(a), lineEnd(r))`, widened to the whole of every run it touches (P..Q).
3. New line starts = P's lines before `a`'s line; `lineStart(a)`; one per `\n` in the inserted
   bytes (`IndexOf` over the piece table, looping on `GetContiguousSpan` — the only bytes scanned);
   Q's lines after `r`'s line, shifted by `ByteDelta`. Rebuild `RowPrefix` with `RawLineRows.Count`
   (no peeks). Split into runs of ≤ `MaxLinesPerRun`.
4. `OriginalStartRow/Line` from P or the original; `OriginalRowsEnd` and `OriginalLinesReplaced`
   from Q (`Q.OriginalStartLine + Q.OriginalLinesReplaced − P.OriginalStartLine`) or from
   `LineEndContaining` and the original line number of `r`. Replace P..Q with the new run(s),
   merge with an abutting neighbour if the result fits the cap, `Renumber` from P's index,
   `NeedsRebuild = HeldLines > MaxHeldLines`.

**Budget.** `MaxHeldLines = 512 · 1024` line records (12 bytes each, ~6MB), replacing
`MaxHeldAnchors`; the constructor's test seam stays. `CanAbsorbEditAt` keeps its meaning: over
budget, an edit in a new place is refused; inside or at the end of an existing run is always
allowed. Undo and redo need nothing new: the journal's reversed extents go through `ApplyEdit`.

**Diagnostics.** `RowsWalkedInLastEdit` → `BytesScannedInLastEdit` and `LinesRebuiltInLastEdit`;
`HeldAnchors` → `HeldLines`; `RawSpanSnapshot` drops `OriginalAnchor`, `AnchorsHeld`,
`ConvergedOriginalRow`, `EditReach` and gains `LinesHeld`, `OriginalRowsEnd`. Update
`RawEditSnapshot`, `RawEditController.Describe`, and `Diagnostics/RawEditInspectorWindow`'s
budget line and span columns. `RawEditMapView` reads only `StartOffset`/`EndOffset` and is
unaffected. The word "span" is kept in public names: it still means a contiguous region of rows
the index describes itself rather than by displacing the original.

### 5. Tests

- `RawSegmentIndexTests.NaiveScan`: track `lineStart` and `cap` and apply §1. Change
  `InRange(len, 1, wrapWidth + 1)` to `wrapWidth + MaxUtf8Backoff + 1`. The pinned offsets in
  `MultibyteCharStraddlingTheCap_BacksOffToTheCharBoundary` and
  `PureContinuationBytes_BreakAtTheCapRegardless` are unchanged by the new rule — verify rather
  than assume. Add cases for `LineStartContaining` / `LineEndContaining`: inside a bucket, across
  bucket edges, inside a long line spanning many buckets, at EOF with and without a trailing `\n`,
  on an empty source.
- `RawLongLineReflowTests`: keep as the measurement but invert the claim: after an insert, later
  boundaries inside the line move by 0 over ASCII and by at most 3 over multi-byte content, and
  never chain.
- `RawEditedRowIndexTests`: every `AssertMatchesAFreshIndex` test survives unchanged (the oracle
  is a fresh index over the edited bytes). Rewrite the "anchor boundaries" section as "line
  boundaries": edits in the same line share a run; adjacent lines merge; one untouched line between
  gives two runs; a delete spanning runs leaves one; a `\n` inserted onto a soft-wrap boundary, at a
  line start, at EOF; a run reaching `MaxLinesPerRun` splits and still matches the oracle. Replace
  `HoldsAnchorsRatherThanEveryRow` / `ResumesTheWalk` / `AnEditPastALargeSpan` with: an edit at
  byte 100 of a 400KB line holds 1 line record and scans 4 bytes; a second edit near its end scans
  1 byte and rebuilds 1 line; `EnoughSeparateEditSites_RaiseNeedsRebuild` counts lines. Add a
  32MB-line case asserting `BytesScannedInLastEdit` equals the insert size.
- `RawEditSnapshotTests`: field renames only; the invariants (ordered, disjoint, deltas add up) stay.
- `RawEditKeystrokeBenchmarks`: parameterise `LongLineBytes` over 8MB and 128MB and add typing
  1024 bytes before the end; all long-line cases should be flat and within ~2× of `TypeCharacters`.
- `RawRowDecoderTests`, `RawCaretStopsTests`, `RawWordStopsTests`, `RawCaretReadoutTests`,
  `RawViewVirtualizationTests` index single-row strings and should not move; run them to confirm.

### 6. Documentation

- `docs/long-line-reflow-options.md`: status → decided and built, pointing here.
- `docs/editing-options.md` §"What typing became": spans are line runs; drop the "line really
  must be walked" paragraphs. `docs/roadmap.md`: close the bullet; the `NeedsRebuild` bullet now
  counts lines. Rewrite `RawEditedRowIndex`'s class remarks.
- `CLAUDE.md`: one short section — *row boundaries are cap-anchored*: a forced break is measured
  from the line's arithmetic cap, so a line's rows are `RawLineRows` arithmetic; new code that
  walks rows uses `RawRowCursor`; never derive a boundary from the previous row's end.

## Sequencing

1. `RawRowBoundary` rule, `RawRowCursor`, `RawLineRows` and its property test; `RawSegmentIndex`
   anchors, walks and the two new queries with tests; `NaiveScan`; `RawLongLineReflowTests`.
2. New `RawEditedRowIndex`; the `RawEditedRowIndexTests` rewrite; snapshot, controller,
   inspector, benchmarks.
3. Docs and `CLAUDE.md`.

Steps 1 and 2 should land as one commit. The old `RawEditedRowIndex` is not correct under the new
rule (its convergence proof assumes a row's future depends only on the bytes from its start, and
it now also depends on the cap phase), so keeping the suite green between the two steps would
mean patching a class that is being deleted.

## Files

Modify: `Argonaut/Features/Raw/RawRowBoundary.cs`, `RawSegmentIndex.cs`, `IRawRowIndex.cs`,
`RawEditedRowIndex.cs` (rewrite), `RawEditSnapshot.cs`, `RawEditController.cs`,
`Argonaut/Diagnostics/RawEditInspectorWindow.cs`; tests `RawSegmentIndexTests.cs`,
`RawEditedRowIndexTests.cs`, `RawLongLineReflowTests.cs`, `RawEditSnapshotTests.cs`,
`RawEditKeystrokeBenchmarks.cs`; the docs in §6.
New: `Argonaut/Features/Raw/RawRowCursor.cs`, `RawLineRows.cs`, `Argonaut.Tests/RawLineRowsTests.cs`.
Untouched: `RawPieceTable`, `RawEditJournal`, `RawRowDecoder`, `RawRowReader`, `RawCaretStops`,
`RawTextSurface`, `RawWordStops`, `RawCaretReadout`, `RawTextExtractor`, `RawOffsetRowResolver`.

## Out of scope, noted for later

- `RawCaretReadout`'s 1MB column-scan cap could become exact through `LineStartContaining`.
- `ProduceRows` could scan per line (`IndexOf('\n')` over the whole line, then arithmetic anchors)
  rather than per row — a scan speedup the new rule makes possible, not needed for this fix.
- Option B (re-derive on idle) is unnecessary once the per-keystroke cost is constant.

## Verification

1. `dotnet test` — the whole suite, including the geometry property test and the oracle scripts.
2. `RawEditKeystrokeBenchmarks` in Release: the long-line cases flat across 8MB and 128MB, within
   ~2× of `TypeCharacters`, and allocating nothing per keystroke.
3. By hand, on the 4GB test document: enable Edit, type at the start of the ~54MB line; the
   inspector (Cmd/Ctrl+Shift+D, Debug build) should show one span holding 1 line, 1 byte scanned,
   and no perceptible lag. Then insert a `\n` mid-line and undo it; then edit just past the line's
   end; then paste a few thousand lines and type inside the paste.
