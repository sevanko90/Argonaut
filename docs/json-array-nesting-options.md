# JSON array table — nested values, options considered

Decision record for how the array-as-table view should render elements that contain **nested
objects and arrays**. Sibling to [json-array-table-options.md](json-array-table-options.md),
which decided where the table lives and who owns its mapping; this one decides what a cell shows
when the value is not a scalar.

Cost claims below were checked against the code on branch `json-array-viewer`, 2026-08-31, with
file/line references kept so a future reader can tell whether the reasoning still holds.

## Decisions at a glance

- **Cell for a container** — stays a summary badge (`{ 6 members }`, `[ 2199 items ]`) by default.
  Rejected: replacing it with an inline preview of the raw bytes.
- **Getting at the nesting** — three concepts together, not one:
  1. **Columns are routes** (`geometry.coordinates[0]`), expanded and collapsed from a
     **clickable, link-styled column header**.
  2. **Array expansion is capped** at N index columns (default 4, user-settable), with a
     remainder column so nothing disappears silently.
  3. **A cell-scoped detail pane**, independently scrollable — click a *cell*, see that value in
     full beside the still-visible grid.
- **What sets the defaults** — a classification recorded during the discovery walk that already
  happens. Free: it records two numbers the walk has in hand.
- **Rejected**: drill-down navigation as the primary answer (loses context on deep nesting), a
  row-scoped detail pane (returns you to the JSON tree you came from), unbounded flattening of
  nested arrays into columns.

## Where it stands today

Every container cell goes through one branch:

```csharp
// JsonArrayRowCollection.TextFor
=> IsContainer(token.Kind)
    ? rowFactory.BuildContainerSummary(tokenIndex, token, expanded: false)
    : rowFactory.BuildScalarText(token, out _);
```

and column discovery (`JsonArrayTableViewModel.BuildPropertyStructure`) walks **direct children
only**, skipping each nested container whole via `EndIndex + 1`. So nesting is invisible by
construction, at any depth: a GeoJSON feature table is four columns of `{ 2 members }` /
`{ 6 members }`, and a Keepa offer table shows `offerCSV` as `[ 2199 items ]`.

That default is *correct* for the big-file budget — it is what keeps a row's realization cost
proportional to the element's direct children rather than its whole subtree. The problem is that
there is no way to ask for more.

## 1. Options weighed

### A — Inline byte preview instead of the summary

Render a whitespace-collapsed slice of the container's raw bytes, hard-capped at ~100 chars:
`{"type":"Point","coordi…`.

- Cheaper than what is there now: one bounded `mmap.GetSpan` copy, no child walk at all, where
  `DescribeChildCount` walks (capped, cached) child tokens.
- **Rejected on comprehension, not cost.** A truncated brace-soup fragment tells the reader
  little more than the count does, and it burns the column width that a real answer needs. The
  same 100 characters spent on `geometry.type` + `geometry.coordinates[0..3]` say something.
- Still available later as the *rendering of a collapsed container cell* if the badge proves too
  thin; it changes no structure.

### B — Drill-down: click the cell, open that container as its own table

The machinery exists — `ArrayTableService.Request(new ArrayTableRequest(path, offset, length,
originPath))`, and a token hands over offset and length for free. Cost is one sub-range session,
independent of the parent's size.

- **Rejected as the primary answer: it is easy to get lost.** Each hop replaces the whole view,
  and `JsonArrayTableToolbarViewModel` holds a single `OriginPath` with Back meaning "reload the
  origin file" — so three levels down there is no context on screen saying where you are, and no
  cheap way back to level two.
- Would need a breadcrumb stack to be usable, and even then it answers a different question
  ("show me this one thing alone") than the table view is for ("compare this field across rows").
- Keep as a *secondary* gesture on the remainder column for genuinely huge nested arrays, where
  a table of its own is the right destination. Not built in the first pass.

### C — Row-scoped detail pane

Selected *row* rendered as a JSON tree in a side pane.

- **Rejected: full circle.** One element's tree beside the grid is the original JSON view with
  the parent node collapsed. It adds a pane and no information.

### D — Columns are routes, expanded from the header (chosen)

`geometry` becomes `geometry.type` and `geometry.coordinates` on demand. Designed in §2.

### E — Cell-scoped detail pane (chosen)

Click a **cell**; the pane shows *that value*, independently scrolled, with the grid still on
screen as the context. Designed in §3. This is what makes it not option C: the pane is showing
one cell at the depth you clicked, not the whole row from the top.

### F — Classify containers during discovery (chosen, free)

Not a rendering — a policy input. Designed in §4.

## 2. Design — columns are routes, headers are links

### The model

Column identity stops being a property name and becomes a **route**: the steps from the element
root to the value, each step either a property name (raw UTF-8, compared against the mapping) or
an array index.

```csharp
readonly record struct RouteStep(byte[]? Name, int Index);   // Name null => array index
sealed record ColumnRoute(RouteStep[] Steps, string Display); // "geometry.coordinates[0]"
```

This replaces `columnNameBytes` and `ColumnFor` in `JsonArrayRowCollection`. Names stay raw
UTF-8 for the reason the current code states: the column names were discovered from raw bytes, so
matching raw keeps the table and the tree from ever disagreeing about which property a cell is.

### Realizing a row stays bounded — the load-bearing part

Alongside the routes, the shape carries a small **`ExpandedRoutes`** tree: one node per container
the user has opened, children keyed by the next step. (One node per *open* container, so it is
as small as the user's clicks — three expansions is three nodes, not a copy of the document's
shape.)

Row realization keeps today's single pass over the element's children and asks that tree at each
container:

```csharp
// no node for this child => nothing below it is expanded
child = routes.Enter(info) is { } inside
    ? DescendInto(inside, child, info)      // walk the subtree the user asked for
    : info.EndIndex + 1;                    // skip it whole, exactly as today
```

So a collapsed column costs what it costs now, and total per-row work is **the element's direct
children plus the tokens inside explicitly expanded subtrees**. No depth recursion ever happens
on its own: depth grows only by a click.

### Array expansion is capped

Expanding an array column yields `K = min(widest arity seen in the sample, ArrayExpandLimit)`
index columns — default **4**, user-settable from the toolbar. If any sampled element was wider
than K, one extra **remainder column** (`coordinates[…]`) renders that element's tail as the
ordinary badge, so nothing vanishes silently and the reader can see there is more.

This is what keeps `$.features[7].geometry.coordinates[7982][2]` finite: expanding
`coordinates` costs 4 cells plus a remainder, never 7982. `coordinates[0]` is itself an array,
so it renders as `[ 2 items ]` with its own expand link, and the same cap applies again at that
level.

### Header UI

The header shows the full route with **each segment as a hyperlink-styled run** (underlined on
hover, hand cursor):

```
geometry.coordinates[0]
└─link─┘└────link─────┘
```

- Click a **leaf that is a container** → expand one level.
- Click any **ancestor segment** → collapse back to that level.
- A column whose value is a scalar has no link, and is plain text. The affordance itself is
  therefore the signal for which columns have something inside them.

`TableViewColumn.Header` is typed `object` and `HeaderTemplate` is already a per-column property
([TableGridColumns.cs:113](../Argonaut/Features/Csv/TableGridColumns.cs)), so this is a header
template plus a command — no changes to TableView, and no second header row.

### What an expand or collapse costs

The same as a reshape does today: re-walk **only the sampled elements** (`InitialElementTarget`,
250) at the affected route to discover the child names, arities and widths, build a new
`CsvStructure`, then `SetShape` → cache drop → `Reset`. No file re-scan, no re-index, no re-walk
of the array. Which also means expansion inherits the sample's honest limitation, the same one
the top-level columns already have: a key that first appears at element 40,000 has no column.

### Two hazards to design around

- **`TableGridColumns.SameShape` compares column count and `MaxChars` only**
  ([TableGridColumns.cs:139](../Argonaut/Features/Csv/TableGridColumns.cs)). An expand/collapse
  that happens to produce the same count and widths would be taken for a relabelling, leaving the
  cell templates bound to the previous columns' indices. Route identity has to enter that
  comparison before this ships.
- **Header clicks are input-path re-entrancy** in the same family CLAUDE.md documents: the click
  handler replaces the collection the grid is bound to. It is a `Click`, not a selection commit,
  so the `SchemaRootPicker` crash does not apply verbatim — but the reshape must not run inside
  anything still enumerating the old columns. `UiDeferral.AfterCurrentInput` is the cheap
  insurance.

### Guard

Hard cap the total column count (~150). Expansion past it refuses with a toast rather than
building the grid — a 4,000-column table helps nobody and the width maths is O(columns).

## 3. Design — the cell detail pane

Click a **cell**: a right-hand pane opens behind a splitter, with its **own scrollbar**. The grid
does not move.

- **Large scalar** → the full, untruncated text, wrapped and scrollable. This is the answer for a
  column of long strings, which the grid caps at 40 characters
  (`CsvStructure.MaxDiscoveredChars`) and cannot usefully widen.
- **Container** → the ordinary JSON tree scoped to that token's range, reusing `JsonRowFactory`
  and a scoped visible-row collection. Virtualized like every other tree in the app, so
  `offerCSV` with 2,199 items opens instantly.

Cost is bounded by the one subtree being shown, whatever the file's size. Keyboard movement down
a column keeps the pane following the selected cell — that is the "read this nested field down
the rows" workflow, and it is the case a drill-down navigation (option B) serves worst.

## 4. Design — classification, and what it decides

The discovery walk already visits every direct child of every sampled element. For a container
child it has, in hand and for free, `token.Length` (byte size, unpacked O(1) from the token log)
and the child count it computes for the summary badge. Recording both per column buys:

- **which headers get an expand link at all** — a column that is a scalar everywhere gets none;
- **the default open state** — a uniformly small, uniformly-sized array like GeoJSON's `bbox`
  (4 numbers) can open already expanded, while anything large or ragged (`offerCSV`,
  `properties`) opens collapsed;
- **the arity** that caps array expansion, per §2.

No extra walk, no extra read, no decoded text.

## 5. Build order

1. **Routes and `ExpandedRoutes`, everything collapsed.** Classification recorded. Behaviour
   identical to today's, new machinery underneath — testable end to end with no UI at all.
2. **Clickable headers**, expand/collapse, the array cap and its toolbar setting, the column-count
   guard, and the `SameShape` fix.
3. **The cell detail pane.**

Each slice is shippable on its own and none of them changes the collapsed-cell cost, which is the
budget the whole feature has to live inside.

## 6. Open questions

- **Does an expansion survive Back and re-entry?** The table document is rebuilt from scratch on
  every entry, so expansions are lost unless they are carried in the `ArrayTableRequest`. Cheap
  to add later; not obviously wanted.
- **Sort/filter by an expanded column** is a full scan, and therefore a separate feature with its
  own budget. Expansion must not quietly become the thing that makes people expect it.
- **Remainder column gesture.** Option B (open that array as its own table) is the natural
  destination for a click on `coordinates[…]`, and would need the breadcrumb stack B was rejected
  for lacking. Deferred until the pane proves insufficient.
