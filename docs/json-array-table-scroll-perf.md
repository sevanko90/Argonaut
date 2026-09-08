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

Headless measurement, 13 visible rows:

| columns | cells realized | ms per one-row scroll step |
| ------- | -------------- | -------------------------- |
| 97      | 1261           | ~24–31                     |
| 10      | 130            | ~1.3                       |

Only about 10 columns fit on screen at 1400px, so ~87 of every 97 cells realized are off-screen.
Against a 16.6 ms frame budget, a single row of scrolling cannot complete inside a frame — that is
the stutter.

Within the same run, the cell template also matters:

| 97 columns          | ms/step |
| ------------------- | ------- |
| click-hint cells    | ~24     |
| plain cells         | ~8      |

The array table passes `clickHint`; the CSV grid does not, which is why only this grid stutters.

## Methodology warning — read before trusting a number

Cross-run comparisons in this harness are **not reliable**. Repeating the untouched baseline gave
24.66 ms and 31.32 ms on consecutive runs: a 6.7 ms spread, larger than most changes worth making.

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

   Measured click-hint overhead (the in-run `hint=yes` ÷ `hint=no` ratio) fell from **~3.0× to
   ~1.8×**. Treat the residual as indicative, not precise: a control run measuring two
   *identical* configurations against each other came out 20.19 vs 15.62 ms, so even the in-run
   ratio carries roughly ±30%. A 3× gap is well outside that; a 1.8× one only barely.

   What is not noise-dependent is the structural claim: the per-cell work is strictly smaller,
   because three controls per cell became zero.

   The remaining gap is most likely `CellTip`, the tooltip built eagerly for every cell (four
   controls and five bindings) for a popup only one cell ever shows. It cannot simply be built on
   first hover — Avalonia's tooltip service hooks the control when `ToolTip.Tip` is set, so
   setting it during the hover that would show it means the first hover silently does nothing. A
   lightweight `Tip` value plus a shared `DataTemplate` that materialises the visual only when
   shown is the shape worth trying next.

2. **Reduce the column count** (not done — the ~20× lever). Nothing can be done inside `TableView`
   to virtualize columns, so the only way to realize fewer cells is to create fewer columns: show a
   subset by default and let the reader bring more in. 97 columns is unreadable anyway, so this is
   a usability change as much as a performance one, and it needs a design decision rather than a
   patch.
