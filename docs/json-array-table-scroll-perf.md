# JSON array table — scroll performance

Findings from profiling a 6.8 MB Keepa result (`products`: 1422 elements, 97 discovered columns),
reported as stuttering when scrolled. 2026-09-08.

## The data layer is not the problem

Measured against the real file, through `JsonArrayTableViewModel` + `JsonArrayRowCollection`:

```
LoadAsync (to first paint)   :  93.2 ms
columns                      :  97
rows                         :  1422
realize all 1422 rows cold   :  67.1 ms   (0.047 ms/row)
simulated scroll, 13920 gets :  61.0 ms   (0.129 ms per 3-row step)
```

The element index, the LRU row cache and the bounded per-element walks all behave. Nothing here is
worth optimising for this symptom.

## The cost is realized cells, and there are far too many

Avalonia 12.1's `TableView` realizes a cell for **every column of every visible row**. It has no
column virtualization — `TableView`'s whole property surface is `CanUserResizeColumns` and
`Columns`; there is no knob to enable it.

Headless measurement, 13 visible rows, settled ms per one-row scroll step (first pass discarded —
see the methodology warning below, it matters by 2–3×):

| 97 columns, 1261 cells | current | cells stripped to the bare minimum |
| ---------------------- | ------- | ---------------------------------- |
| click-hint cells       | 10.79   | 7.45                               |
| plain cells            | 7.26    | 5.74                               |

| 10 columns, 130 cells | current | stripped |
| --------------------- | ------- | -------- |
| either template       | ~0.43   | ~0.36    |

Only about 10 columns fit on screen at 1400px, so ~87 of every 97 cells realized are off-screen.

**The count dominates everything else, and it is not close.** With the cell reduced to its floor —
one `TextBlock`, one binding, no tooltip, no mark, font by inheritance — 97 columns still costs
~16× what 10 columns costs. Emptying the cell buys about 1.5–2×; realizing only the visible
columns would buy roughly 16×. That is why the remaining work is column virtualization and not
further cell tuning: there is barely anything left in the cell to remove.

The array table passes `clickHint` and the CSV grid does not, which is why only this grid was
reported as glitchy — but even the plain template is 7.26 ms/step at 97 columns, so a wide CSV
would stutter too.

## Methodology warning — read before trusting a number

**Discard the first pass.** Whichever configuration a process measures first pays JIT and
first-window costs, and it is worth 2–3×: the same 97-column config measured 16.64 ms on its
warm-up pass and 7.45 ms once settled. The harness sweeps configurations in a fixed order, so for
a long time the first one measured always looked catastrophically slower than the rest — and every
"the click-hint template costs 3×" figure taken from a single pass was mostly that artefact. The
tell that finally exposed it: two *identical* configurations measured 16.88 vs 9.49 ms.

**Cross-run comparisons are unreliable.** Repeating the untouched baseline gave 24.66 ms and
31.32 ms on consecutive runs: a 6.7 ms spread, larger than most changes worth making.

An earlier pass "showed" a 29% win from caching a per-cell `Geometry.Parse`, from comparing two
single runs. Repeating it showed the effect was entirely inside the noise, and the change was
reverted. Three micro-optimisations were tried and none survived repetition:

- caching the hover mark's parsed `Geometry` (it was re-parsed per cell);
- replacing the mark's per-cell `FindAncestorOfType<TableViewCell>` + binding with a style selector;
- swapping the cell's `Grid` wrapper for a lighter `Panel` (this measured *worse*).

Only compare configurations measured **inside a single process**, which is why the harness sweeps
column count and template shape in one run. Absolute figures are Debug builds in a Linux container
and will not match a Release build on Windows; the ratios are what carry.

## Fixes

1. **Single shared hover-mark adorner** (done — `TableGridColumns.HoverMarkAdorner`). The
   click-hint template wrapped every cell in a panel holding the text plus a `Border`+`Path` mark,
   so the mark was built 1261 times over to show the one the pointer is on. One adorner now moves
   to whichever cell is hovered, and a cell is a single `TextBlock` again — about 3,800 fewer
   visuals in a 97-column viewport.

   Honest accounting: the "~3× click-hint penalty" this was sold on was largely a measurement
   artefact (see the methodology warning - the first configuration measured in a process is
   2-3× slow). Re-measured properly, click-hint cells at 97 columns went from 10.79 ms/step to
   7.45 with the cell stripped entirely, so the mark was worth a part of that, not a 3×.

   The change is still right - three controls per cell became zero, which is strictly less work
   - but it is a modest win, not the fix.

   The remaining gap is most likely `CellTip`, the tooltip built eagerly for every cell (four
   controls and five bindings) for a popup only one cell ever shows. It cannot simply be built on
   first hover — Avalonia's tooltip service hooks the control when `ToolTip.Tip` is set, so
   setting it during the hover that would show it means the first hover silently does nothing. A
   lightweight `Tip` value plus a shared `DataTemplate` that materialises the visual only when
   shown is the shape worth trying next.

2. **Realize only the columns in the horizontal viewport** (not done — the ~16× lever, and the
   only one left that matters).

   Picking a default subset was considered and rejected: Argonaut is a generic viewer, so there is
   no heuristic for which of an arbitrary document's 97 properties are the useful ones. "The first
   25, whatever they happen to be" is not a feature.

   Two ways to get there:

   **a. Window the columns inside `TableGridColumns`** — keep all 97 logical columns, but only
   ever hand `TableView` the ~15 in view, plus a leading and a trailing spacer column whose widths
   are the sums of the hidden ones so the horizontal scroll extent stays honest. Re-window on
   horizontal scroll. Days, not weeks, and no new control. Unproven: spacers need empty headers
   and no resizer, every index crossing the boundary (fit-to-content, `ShowCell(row, column)`,
   header expansion) needs logical↔realized mapping, and horizontal scrolling may judder where
   vertical no longer does. Worth a timeboxed spike before committing.

   **b. Write a properly virtualizing grid control** — 2–3 weeks. The layout maths is the easy
   part; the cost is cell recycling (this file bakes the column index into each cell template, so
   recycling a cell from column 3 into column 40 needs one shared template that rebinds on reuse),
   shared column geometry invalidated on every drag, scroll anchoring when widths change, keyboard
   navigation across unrealized columns, and automation peers. It also means rebuilding what
   `TableView` was adopted *for* — the sticky header, horizontal-scroll tracking and resizer that
   `JsonArrayTableView.axaml` notes were hand-built before.
