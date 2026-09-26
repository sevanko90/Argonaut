# JSON diff: a sparse edit script on the tree surface

Supersedes the record log and the `ListBox` view of `json-diff-plan.md`, and is step 13 of
`json-sparse-index-plan.md`. What that plan settled and this one keeps: Merkle content hashes on
the sparse index's budget, objects matched by decoded name, arrays aligned by histogram anchors
with Myers in the gaps, unique exact-hash moves across parents, both documents indexed before the
comparison starts, find over both files interleaved into one sequence, and the two-session
teardown order in `JsonDiffSession`.

What changes, and why:

1. **The log records differences, not nodes.** Today every sibling at a level that changed gets
   an `Unchanged` record, so one edited member of an object with a million keys costs a million
   records, and an array can only be compared at all below a 100K-element cap. Consecutive
   unchanged siblings become one **run** record, so the log grows with the number of changes,
   not with how wide the changed levels are.
2. **Arrays are trimmed before they are aligned.** The common prefix and suffix are compared by
   streaming both sides' element hashes, forward and then backward through the sparse index's
   checkpoints, holding nothing. Only the middle is aligned, and only the middle is capped. An
   append to, or one edit in, a ten-million-element array is exact and costs one pass.
3. **An over-cap middle is compared in place, then given up on in one record.** Pairs are
   streamed in step; equal ones join runs; where a pair differs, a short look-ahead on each side
   finds a few inserted or removed elements, and otherwise the pair is descended. Past a record
   budget the rest of the middle becomes one **range** record: the left elements and the right
   elements, shown whole, not aligned.
4. **Arrays of records are matched by their identity key** when they have one - an `id`-like
   member present, scalar and unique in every element on both sides. A reordered and edited list
   of API objects then reads as "moved" and "modified" rather than a wholesale rewrite.
5. **The diff draws on the tree surface.** A merged-tree cursor walks the log and hands over to a
   `TreeCursor` on one side inside regions the log does not describe node by node; a two-pane
   painter draws both sides of each row. The materialised row list, its display cap, and the
   full re-walk on every toggle go.

## The record log

Records are published in merged depth-first order, as before, and a descended container's
children are the records that follow it up to its `SubtreeEnd`. What a record covers:

| Record | Own row | Left | Right |
|---|---|---|---|
| **Run** (`Unchanged`) | none - one row per pair | `LeftCount` consecutive children from `LeftOrdinal` | the same number, consecutive from `RightOrdinal`, equal pair by pair |
| **Modified**, descended | the pair's containers | the container | the container |
| **Modified**, undescended | the pair | a scalar, or a container of the other kind | likewise |
| **Added** / **Removed** | the node | - / the node | the node / - |
| **Moved** (either end) | the node at that end | as reconciled | as reconciled |
| **Moved** destination, changed | the node, descended | the source's container | the container |
| **Range** (`Modified`, `IsRange`) | a header | `LeftCount` children from `LeftOrdinal` | `RightCount` children from `RightOrdinal` |

Each record carries both sides' first node, ordinal, count, end and document depth, and a
**left anchor**: the
offset in the left document at which it sits in merged order - its own left row start when it
has a left node where it stands, otherwise the end of the left node before it. Anchors never
decrease along the log, which is what lets a scroll position or a left-document offset find its
record by binary search. A run's right nodes are equal to its left ones by hash, and the panes
draw a run from the left document (the right pane's JSONPath is spliced from the run's
`RightOrdinal`); inside any unchanged node the right pane mirrors the left, as it does today.

A run joins siblings only while both ordinals advance together. Reordered object keys therefore
split runs - correct, and still bounded by the number of places the order changed.

The one exception to merged order is a move paired by similarity (step 5): its pass runs after
the descent, so the destination's children are appended after everything else and reached
through its `FirstChild`/`ChildrenEnd`. They take the destination's anchor; `MainRecordCount`
marks where the descent's records end, and only those are searched by anchor.

## Comparing a level

For a pair of same-kind containers whose hashes differ, with `n` and `m` children:

1. **Prefix.** Stream both sides' children from the start while the pairs are the same (arrays:
   equal hash; objects: equal decoded name and equal hash). They form one run.
2. **Suffix.** Stream backward from both ends, no further than the prefix, the same way. A
   large container is read backward one checkpoint span at a time
   (`SparseContainerIndex.FindResumePoint(container, ordinal)`); a small one is read once.
3. **Middle.** What is left of each side:
   - **Objects** are matched by decoded name, uncapped (memory is the width of the changed
     middle, as today).
   - **Arrays** within `MaxAlignableArrayElements` per side: if an identity key is found, pair
     by key; otherwise histogram anchors and Myers in the gaps, as today.
   - **Arrays** over the cap: compared in place, streamed, with a look-ahead of
     `ResyncWindow` elements on each side to resynchronise after an insertion or removal and
     `MaxPositionalRecords` as the budget; the container is flagged `IsAlignmentApproximate`,
     and anything past the budget is one range record.
4. **Suffix run**, then the level is done.

Every aligned pair whose hashes are equal extends the current run; every other outcome closes it.

### Identity keys

Tried only for an in-cap array middle whose elements are all objects on both sides. Candidate
names come from the first left element's scalar members whose name looks like an identifier
(`id`, `_id`, `uuid`, `guid`, `key`, or ending in `Id`, `ID`, `_id`, `-id`), at most four, in
member order. The first candidate that is present with a string or number value in every middle
element on both sides, unique within each side, and shared by at least one pair, is the key.
Pairs are then made by key value; a longest increasing subsequence of their right ordinals is the
stable order; an equal pair outside it is `Moved`, a different one is a descended `Modified`
flagged `IsMovedWithin` (badged with its old index). Unpaired elements are `Added`/`Removed`.
Choosing a key by hand is later work.

## The merged tree on the surface

`TreeSurface` shows anything that can produce rows, not only a byte tree:

- **`ITreeRowCursor`** (`Engine/Indexing/Trees`): current row, ancestors, start, end, next,
  previous, seek, clone. `TreeCursor` is one; the diff's merged cursor is another.
- **`ITreeRowSource`** (`Ui/Tree`): what the surface asks of a tree - a cursor, the painter and
  gutters, expansion (toggle, set, deep expand and collapse, whether a collapsed row hides a
  position), and the scroll model (a length, a row's scroll position, seeking a cursor to one).
  `TreeDocument` implements it over a sparse index exactly as the surface did inline.
- **`TreeRow.Detail`**: an object a non-byte cursor attaches to its rows; null from `TreeCursor`.
- **Panes**: a painter may draw a row as two side-by-side panes (`PaneCount`, `AppendPaneRuns`,
  `PaneMarker`, `PaneTint`). Each pane indents by depth and draws its own arrow; tints come from
  the surface's `TintBrushes`. Two panes do not pan: each is clipped to its half.
- **`TreeExpandState.WithDefaultDepth`**: another default over the same overrides, so a walk
  inside a region can open everything above it without disturbing what the user opened below.

### Row keys

A merged row's key is its `Start` and its node's `ValueStart`: `record << 39`, plus for a row
inside one of the record's regions `region << 38 | (row start - region base + 1)`. Keys follow
merged order, so find's order key is the key of the row showing a match; a key need not be a
row start - seeking to one inside a region lands on the row showing that offset.

### Regions

A record's region is a span of siblings on one side - its node's children, a run's pairs, or a
range's side - walked by a `TreeCursor` whose expand state opens everything above the span and
the user's own choices beneath it (close rows are skipped; the diff has none).

| Record | Regions |
|---|---|
| Run | its pairs, drawn from the left into both panes |
| Removed, Moved source | the left node's children |
| Added, Moved destination | the right node's children |
| Modified, undescended container | the left node's children, then the right node's |
| Range | the left elements, then the right elements |

Expansion: a descended record starts expanded, everything else collapsed; records toggle by
index, region rows by side and value start.

### Scrolling

Estimated, like the JSON tree's, but over the left document: a row's scroll position is its left
row start, or its record's left anchor where it has none. Seeking a scroll position binary-searches
the anchors and seeks within the region that covers it.

### What moves off the list

Selection and the context bar read the selected row's detail. Next/previous change steps the
records in merged order and reveals by key. "Changes only" skips runs. Find maps a match to the
key of the row drawing it, or no stop. Before any record exists the surface shows the left
document as a plain JSON tree. `JsonDiffRowCollection`, `JsonDiffRow`, `JsonRow`,
`JsonRowPresenter`, `DepthToMarginConverter` and the display cap go; `JsonRowStyles` keeps only
the link-button styles the path and context bars use.

## Order of work

Status: steps 1-5 done; the diff view awaits a manual check.

1. [x] **The surface takes any row source.** `ITreeRowCursor`, `ITreeRowSource`, `TreeRow.Detail`,
   panes and tints, `WithDefaultDepth`. The JSON tree and the cell pane behave exactly as before.

   The painter's pane members default to one pane, so `JsonTreePainter` is untouched. A pane
   draws its arrow only where it has text, so a one-sided row has one arrow. The pane marker is
   still a string; the changed-path dot is a leading run in the new `TreeRunStyle.Change`.
2. [x] **Runs, anchors, counts and the merged tree.** The log gains runs and per-side ordinals,
   counts, ends and anchors; `JsonDiffTree`, `JsonDiffCursor` and `JsonDiffPainter` draw it on the
   surface; the view model, find and navigation move to keys; the list goes. The two land together
   because the list cannot draw a run.

   A move's badge sits beside its node at the end that draws it (the stub's on the left), not
   always on the right. A record row's context-bar value is the node's collapsed summary rather
   than its bracket. The view model reveals itself (`JsonDiffTree.Reveal`) and sets the
   selection before asking the view to scroll, so next/previous change and find work without a
   view. Tested headless: `JsonDiffTreeTests` (rows, panes, expansion, reveal, seeks against
   walks, scroll positions, find keys), `JsonDiffViewModelTests`, `JsonDiffFindTests`,
   `JsonDiffViewTests`, and the two-pane cases in `TreeSurfaceTests`.
3. [x] **Trimmed and positional arrays.** Prefix and suffix runs, the in-cap middle, the over-cap
   positional walk with its budget, range records.

   Objects are trimmed too, by name and hash. The in-place walk gained the look-ahead: without
   it one element removed near the start of a ten-million-element array, with anything else
   changed near the end, made every element between into a modification.
4. [x] **Identity keys.** Tried before anchors on an in-cap middle of three or more elements;
   `IsIdentityName` is the name rule.
5. [x] **Similarity pairing** for moved-and-edited containers (`json-diff-plan.md`, Stage 2 v2).
   Needs a record's children to be able to live after the end of its subtree - the pass runs
   after the descent - so it adds a first-child link to the log.

   Records also carry each side's document depth, since a pair's two ends may sit at different
   depths and a region walker needs the real one. Identical containers are not scored (exact
   pairing declined them as ambiguous) and a tie for best is no pair. An added container is
   emitted whole, so a block moved *into* a new container is still removed and added.

Needs a manual check: compare two files; changed paths open to the differing leaf; both panes
line up while scrolling, with tints, change marks, array indices and badges; expand unchanged,
added, removed, moved and moved-and-changed rows; a range; the context bar's values and paths;
next/previous change; "changes only"; find across both files; the scrollbar over a long diff.

## Tests

- Differ: runs replace unchanged siblings (a 20K-key object with one edit is four records);
  append to and edit inside an array far over the cap are exact, flagged nothing; an over-cap
  scramble is approximate and ends in one range; prefix/suffix never overlap; identity pairing
  (reorder plus edit gives `Moved` and `Modified` with `IsMovedWithin`; missing, non-scalar or
  duplicate keys fall back); every left and right ordinal covered exactly once over random arrays,
  runs and ranges expanded; recorded and read hashes give identical logs; anchors never decrease.
- Merged cursor: forward and backward walks agree; seek to every key lands on its row; collapsed
  ancestors hide and reveal; changes-only; runs, regions and ranges expand; walks during growth.
- Surface: the existing tree suites unchanged; two panes paint, hit-test arrows and links per
  pane, and do not pan.
- View model: context bar, paths (including under a moved container), next/previous change, find
  stops, as the list's tests did.
