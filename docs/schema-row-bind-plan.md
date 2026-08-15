# Binding a schema type at a row (the row-level bind button)

**Status: deferred, not started.** Deferred for want of a document exhibiting the problem, not
because the design is unresolved. See "Getting test data" below — that is the first task, and the
one that decides whether this gets built at all.

Prerequisite work is done and committed: `docs/schema-root-auto-match-plan.md` steps 1–4
(`JsonSchemaRootMatcher`, `JsonDocumentKeySampler`, the match-ranked picker). This document is
step 5 of that plan, extracted because it is a change to the row walk rather than to schema
selection.

## The problem

A schema type is chosen once, for the whole document, and applies from the root down. That fails
when the document's outermost object is not the thing the schema describes:

```json
{
  "data":   { "asin": "B01", "title": "…", "stats": { … } },
  "meta":   { "page": 1, "total": 250 }
}
```

The root has two keys, `data` and `meta`. They match nothing in any schema, so
`JsonSchemaRootMatcher.Best` correctly declines and no root can usefully be bound. The object one
level down would match a type exactly, but there is no way to say so: the only binding point is
the document root.

This is distinct from *depth*, which already works. Local `$ref`s are materialised at load time
(`JsonSchemaLoader` pass 2), so once the right root is bound, hints flow arbitrarily deep,
including through `additionalProperties` maps — verified against a real OpenAPI document down to
`ShopFlightResponse → offers[k] → Offer → fares[k] → Fare → fareClass`. Depth was never the issue.

## The feature

A small button on a container row: *figure out which schema type this is, and apply it from here
down*. Clicking it samples that node's property names, ranks every named root against them, and
binds the winner at that token.

It also subsumes the root case — the root row is just one container among many — and gives the
user a way to correct a bad automatic choice in place, rather than hunting a type name in a list
of a hundred-odd.

## Why it is a real change

The walk resolves schema nodes strictly top-down. `Rebuild`
(`Argonaut/Features/Json/JsonVisibleRowCollection.cs:728`) seeds the recursion at token 0:

```csharp
AppendSubtree(0, newVisible, arrayIndex: -1, schemaNodeId: schema?.RootId ?? -1);
```

and `AppendSubtree` (`:818`) carries the id down, each child taking one `ResolveMember` /
`ResolveElement` step from its parent's node. That is precisely what makes hints affordable on a
document never held in memory: one `int` per row, one lookup per row, no path building.

There is no way to express "from token 47, use schema node 12 instead". Adding one means touching
the walk every row passes through.

## Design

### Override map

A `Dictionary<int, int>` on `JsonVisibleRowCollection` mapping token index → schema node id,
consulted in `AppendSubtree` when descending:

```csharp
int childSchemaId = overrides.TryGetValue(childIndex, out int pinned)
    ? pinned
    : /* existing top-down resolution */;
```

Cost when empty is one `Count == 0` test hoisted out of the loop, so the no-override case — every
document today — pays nothing measurable. Populated, it is one dictionary probe per *displayed*
child, which is bounded by `ChildCap` per container, not by document size.

A pinned node overrides whatever the parent chain would have produced, including `-1`. That is the
whole point: the wrapper root resolves to nothing, and the pin restores a subtree beneath it.

### Token indices as keys

Token indices are stable within a session — `JsonStructureIndex` is append-only and never renumbers
— so a plain `int` key is safe for the lifetime of the collection. They are *not* stable across a
reload, since the file may have changed on disk.

That makes overrides **session-only**. Persisting them would mean storing a JSONPath and
re-resolving it on open, which drags in `JsonPathResolver` and a whole re-binding step at load. Not
worth it for a first cut; note it as a known limit rather than designing around it.

### Interaction with the root binding

The bound root (`JsonSchemaDocument.RootId`, chosen in the toolbar picker) stays as it is. An
override is a local exception layered on top, not a replacement. Changing the schema or the root
must clear all overrides — they name node ids in a document that no longer applies.
`SetSchema` (`:924`) is the natural place.

### Rebuild semantics

Adding or clearing an override changes stored `schemaNodeId`s but no row structure, so it lands on
the same path as `SetSchema`: full `Rebuild`, `Reset` event, LRU row cache cleared. It cannot be
`InvalidateRealizedRows`, which keeps the existing `VisibleRow` list — the ids live in those rows
and can only be recomputed by re-walking.

### Choosing what to bind

Entirely existing machinery:

- `JsonDocumentKeySampler.ReadMemberNames(index, mmap, containerIndex)` — already public and
  already takes an arbitrary container index, written this way for exactly this purpose.
- `JsonSchemaRootMatcher.Rank` / `.Best` — unchanged.

Declining stays honest: if `Best` returns null (nothing cleared the bar, or the top two are
indistinguishable), do not bind silently. Open the type picker filtered to the plausible
candidates and let the user choose, then pin whatever they pick.

### UI

An icon on container rows, visible only when a schema offering a choice is bound
(`JsonSchemaSettings.RootOptions.Count > 0`) — otherwise it is noise on every row of every
document. Likely a hover-revealed button in the row `DataTemplate`
(`Argonaut/Features/Json/JsonView.axaml`), beside the existing hint/schema-label elements.

Rows carrying a pin need to say so, and need a way to remove it. A pinned row's schema label is
currently indistinguishable from an inherited one.

Open question worth settling before building: does the icon appear on *every* container row, or
only where it would help (rows currently resolving to `-1`)? The latter is far less noisy and
arguably more discoverable, but "no hint here" is exactly where a user is least likely to look for
an affordance.

## Getting test data

The blocker. Needed: a document whose outermost object wraps the real payload, plus a schema
describing the payload but not the wrapper.

Options, cheapest first:

1. **Synthetic fixture.** Wrap an existing test payload in `{"data": …, "meta": …}` and reuse a
   shipped schema. Enough to build and test the mechanism, not enough to judge the UX.
2. **A real wrapped API response.** Many APIs (JSON:API, GraphQL's `{"data": …}`, paginated
   envelopes) have this shape natively. Best signal on whether the affordance is discoverable.
3. **kyteapi with a deliberately wrong root.** Bind `Address` to a booking response and use the
   button to correct it mid-tree. Exercises the correction path rather than the wrapper path.

## Scope guard

Do not let this grow into per-row schema *editing*. It binds an existing named root at a node;
it does not author schema fragments.

## Reasons it may never be worth building

Recorded so the decision is re-litigated on evidence rather than momentum:

- The problem may not arise for the documents actually opened in this app. It has not yet, across
  a real OpenAPI document, a GeoJSON schema and a Keepa response.
- Choosing a better *root* may cover most of it. A wrapper root often has a matching type in the
  schema too (an envelope type), in which case binding that root labels the wrapper and its
  contents without any per-row mechanism.
- It puts a dictionary probe in the walk that every row of every multi-GB document passes through.
  Cheap, but non-zero, and paid by everyone to serve a case that may be rare.
