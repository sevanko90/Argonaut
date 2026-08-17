# Auto-matching a schema root to the open document

## Context

`docs/json-schema-hints-plan.md` assumed one schema file = one schema, so
`JsonSchemaDocument.RootId` was a single node the walk started from. That holds for a hand-written
schema and breaks for an OpenAPI document, where the file's own root carries
`openapi`/`info`/`paths` — no schema keywords at all — and every usable schema is a named entry
under `components/schemas`. A real example (`kyteapi.json`, 584 KB, OpenAPI 3.0.3) holds **119**
of them.

Phase 1 (shipped) makes those loadable and bindable:

- `JsonSchemaLoader` reads `components/schemas` as a third definition container alongside
  `$defs`/`definitions`, which is also what makes `#/components/schemas/…` refs resolve.
- `JsonSchemaDocument.NamedRoots` exposes the independently-bindable schemas;
  `WithRoot(name)` re-points the walk's starting node, sharing the immutable node array.
- `DocumentRootIsUsable` is false for an OpenAPI document, so the picker omits the "whole
  document" entry entirely rather than offering one that would label nothing. There is no "no
  type" entry either: binding the wrong type cannot error, so a root is always bound and "No
  schema" is how you see nothing.
- The toolbar picker is a filterable flyout, not a combo — 119 entries in a flat scrolling list
  is not a usable control. (It began as an `AutoCompleteBox`, which solved filtering but not
  discovery: type-to-filter is no help to someone who does not yet know what to type.)
- The choice persists per document via `SchemaSelectionPreference` (schema path + root name).

Phase 1 leaves the user picking the type by hand, from a list where nothing indicates which entry
matches the file they have open. That is the gap this plan closes.

## Goal

When a multi-root schema is bound, pick the root whose shape matches the open document, and say so
— without ever silently binding the wrong type. Manual override stays; auto-match sets the initial
value, it does not replace the picker.

## Approach

Score each named root by how well its declared property names overlap the document's actual keys.

Every ingredient already exists:

- **Document keys.** The sibling-hop idiom in `JsonVisibleRowCollection.AppendSubtree`
  (`Argonaut/Features/Json/JsonVisibleRowCollection.cs:786`) and `CountDirectChildren`
  (`:620`) walks a container's direct children via `EndIndex`, reading each name as
  `mmap.GetSpan(child.NameOffset, child.NameLength)`. Cap the collected set (~64 keys): more than
  that discriminates nothing further and the cost must stay bounded.
- **Candidate keys.** `JsonSchemaNode.PropertyKeysUtf8` is already UTF-8 and already sorted
  ordinally (that is what lets `ResolveMember` binary-search off the mapping). Sorting the
  document keys the same way makes scoring a merge-intersect of two sorted arrays — no new data
  structure, no allocation per candidate beyond the counters.
- **Threading.** `InferDefaultDateSchemeAsync` (`Argonaut/Features/Json/JsonViewModel.cs:328`) is
  the exact precedent: `WaitForTokenCountAsync` → `Task.Run` the scan → `Dispatcher.UIThread.Post`
  the result, registered through `session.RegisterDependentTask` so the mapping outlives it.

Cost for kyteapi.json: 119 candidates × ~20 keys against ≤64 document keys. Microseconds, once per
bind.

## Scoring

For candidate *C* with property-key set `Kc`, and document key set `Kd`:

```
matched   = |Kc ∩ Kd|
coverage  = matched / |Kd|      // how much of the document the schema explains
precision = matched / |Kc|      // how much of the schema the document uses
```

Rank on `coverage` first, break ties on `precision` — that prefers the tight type over a superset
that happens to contain it. Reject an ambiguous win (top two within `AmbiguityMargin = 0.05` on
both measures). **Showing nothing beats confidently binding the wrong type**, and a near-tie
between `CommitBookingResponse` and `RetrieveBookingResponse` is exactly the case where the user
must choose.

Also require `matched >= 2` — a single-key document root matches dozens of types on one common
name like `data` or `id`.

### Two ways to qualify

A candidate is offered if **either** measure clears its bar:

| path | bar | the schema it describes |
| --- | --- | --- |
| coverage | `coverage >= MinimumCoverage` (0.5), `matched >= MinimumMatchedKeys` (2) | complete — it explains the document |
| precision | `precision >= MinimumPrecision` (0.9), `matched >= MinimumPreciseMatchedKeys` (4) | partial — the document accounts for all of it |

Coverage alone was the shipped rule and it silently excluded every hand-written schema that
documents less of an API than the API returns. The bundled `keepa-product.json` is the case: its
`$defs/product` declares 13 properties, a live Keepa product object carries about a hundred, so a
document that *is* a bare product scores 13% coverage, fails the floor, and lands unranked in the
alphabetical list — the user picks `product` by hand every single time. On precision it is 13 of
13: nothing the type claims is missing.

Each measure fails on its own in a different direction, which is why both bars exist and why the
precision path carries the higher `matched` floor: precision alone makes a three-property type the
answer for any document carrying those three names. Neither path can be relaxed into the other.

Because plausibility is no longer monotone in coverage, `Rank` sorts **qualifying candidates
first**, then by coverage and precision as before. Callers read the head of the list expecting the
plausible ones to be contiguous (`Best` takes `ranked[0]`; the picker's shortlist loop breaks at
the first implausible entry), and a precision-qualified subset otherwise sorts below unqualified
noise that happens to share more names.

`MinimumPrecision` is 0.9 rather than 1.0 so a schema one API version behind still matches, and
`JsonDocumentKeySampler.MaxKeys` had to rise from 64 to 256 for the same reason: a truncated key
sample makes a schema property that sits past the cap look absent, and precision is exactly the
measure that punishes an absent property.

The picker badges a subset match as `13/13 fields` rather than as its coverage percentage — a
correct match wearing a "13%" label reads as a bad guess (`SchemaRootPick.ScoreText`,
`JsonSchemaRootMatcher.IsSubsetMatch`).

### Root shape

- Document root is an **object** → score its own keys.
- Document root is an **array** → score element 0's keys. *Shipped partially*:
  `JsonDocumentKeySampler` samples element 0 and reports it via `matchedElementOfArray`, and the
  picker states "Matched against the first element of the array" so the result cannot be misread.
  Binding the winner as the array's *items* schema is **not** done — `WithRoot` cannot express
  "root is an array of C", and would need a synthetic node holding `ItemsId = C`. Still open.
- Root is a **scalar** → no match, no picker change.

## Known weak spot: partial schemas that are *also* ambiguous

Two partial types both fully contained in the same document (`ProductA` and `ProductB` differing by
one field) tie on both measures and `Best` declines, same as for the envelope case. Correct, but it
means the precision path helps least on schemas that carry several near-identical partial types.
Nothing to do about it without reading values.

## Known weak spot: wrapper roots

`{"data": {…}, "meta": {…}}` yields two keys that match nothing useful. This is common, and the
scoring above correctly declines rather than guessing — confirmed against `kyteapi.json`, where
such a root scores 0% on all 119 candidates.

The fix is the same machinery aimed one level down: **a row-level bind button**, now written up in
`docs/schema-row-bind-plan.md`. An icon on an expanded object/array row that,
when clicked, scores *that node's* keys against every named root and binds the winner there. It
subsumes auto-match (the root row is just one such node), solves wrapper roots directly (the user
clicks `data`), and gives the user a way to correct a bad guess in place rather than hunting a
type name in a list.

That requires binding a schema at a **non-root token**, which the walk does not currently support:
`Rebuild` seeds `AppendSubtree` with `schema?.RootId` at token 0 and node ids propagate downward
only. A per-token override map (`tokenIndex → schemaNodeId`, consulted in `AppendSubtree` when
descending) is the natural shape and is small, but it is a real change to the walk and should be
its own step after plain root auto-match is working.

## Status

Steps 1–4 below have shipped. `JsonSchemaRootMatcher` ranks, `JsonDocumentKeySampler` gathers the
evidence, `JsonViewModel.UpdateSchemaRootMatches` wires them together, and
`SchemaRootPickerViewModel` presents the result as a filterable two-section flyout.

Since then the matcher also qualifies on precision (see "Two ways to qualify"), so a partial schema
pointed at a document it fully describes a slice of — the bundled Keepa `product` against a bare
product object — is recommended and bound instead of being buried in the full list.

**Binding only on an unambiguous win.** `JsonSchemaSettings.SetRootMatches` re-points a *defaulted*
root at `Best`, and never touches one the user chose (or one restored from
`SchemaSelectionPreference`, which was a choice once). Everything short of a clear winner is left
to the picker, which leads with the likely answers. That restraint is what the thresholds buy:
measured against the real `kyteapi.json`, the top two candidates for a response envelope
(`CommitBookingResponse` and `RetrieveBookingResponse`) are *identical* on property names — 5 of 5
each, 100% on both measures. No name-based scorer can separate them, so `Best` declines and both
appear in the shortlist rather than one being picked at random and silently wrong half the time.

Step 5 remains, and is written up separately in `docs/schema-row-bind-plan.md` — it is a
change to the row walk rather than to schema selection, and is deferred for want of a document
that exhibits the problem.

## Build order

1. **`JsonSchemaRootMatcher`** — pure, `(IReadOnlyList<byte[]> documentKeys, JsonSchemaDocument)`
   → `string? bestRoot` plus its scores. No I/O, no threading; unit-testable against
   hand-written key lists and a small OpenAPI fixture. Land this alone with tests.
2. **Document key extraction** — a bounded sibling-hop helper over `JsonStructureIndex` + `MMapFile`,
   mirroring `DateHintInference`'s scan shape. Tests over a real temp file.
3. **Wire-up** — run when *both* the schema and enough of the index are ready. Note the ordering
   trap: `ApplyInitialSchemaAsync` deliberately races indexing, and `rows` may not exist when the
   schema lands, so this cannot be a one-shot at load. Hook it off `SchemaSettings.SchemaChanged`,
   guarded on `RootOptions.Count > 0` and on nothing having been explicitly chosen, and re-run
   when a later schema is bound.
4. **Surface the result** — the picker must show that the type was *guessed*, not chosen, and let
   one click accept or change it. An unlabelled auto-binding that turns out wrong is worse than no
   binding, because the user has no reason to distrust it.
5. **Row-level bind button** — the row icon and the per-token override map. Split out into
   `docs/schema-row-bind-plan.md`; deferred.

## Explicitly out of scope

- Matching on anything but property names (no `type`/`format`/value inspection). It would mean
  reading values during the walk, which is the cost this whole design exists to avoid.
- `discriminator`. OpenAPI's discriminator would identify a `oneOf` branch precisely, but branches
  are merged rather than discriminated (see `JsonSchemaLoader` remarks), and changing that is a
  much larger question than root selection.
- Scanning `paths` for request/response schemas. Every one of them is a `$ref` into
  `components/schemas`, so they add no candidates.
