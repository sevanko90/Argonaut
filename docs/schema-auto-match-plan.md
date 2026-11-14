# Opening matched schemas

Goal: when a JSON or NDJSON document opens with no schema bound to it, find out quickly whether
one of the schemas already in the catalog fits it, and bind that one. It is a ticked option on
the schema flyout, "Open matched schemas", on by default.

Most of the matching exists. `JsonDocumentKeySampler` reads up to 256 of the outermost object's
property names without waiting for indexing, and `JsonSchemaRootMatcher` scores one schema's
types against them. What is new is running that across every schema in the catalog, with a
stricter bar for binding than for ranking, and fitting the result around the bindings the user
chose by hand.

## Decided

- **Every schema is parsed once per run, in the background, and only its fingerprint is kept.**
  A fingerprint is each bindable type's name, sorted property names and sorted required names -
  not the parsed `JsonSchemaDocument`. It is cached by path, last-write time and length, so the
  first document opened pays for the catalog once (milliseconds for a handful of schemas, well
  under a second for dozens) and every later open pays nothing. No cache on disk; it can be added
  behind the same interface if a catalog of hundreds of schemas ever needs it.
- **A stricter bar to bind than to rank.** The picker keeps its ranking and its "plausible"
  threshold; auto-binding needs more (see the rule below), and declines when two schemas fit
  about equally well.
- **`required` is a signal for auto-matching only.** The loader starts reading it, and the
  matcher uses it as a veto and as evidence for small types. It is not validation: nothing
  checks a document against `required`, and a schema the user picks by hand is bound whether or
  not the document carries its required properties.
- **A remembered binding always wins.** Auto-matching runs only when the document has no
  binding at all.
- **Picking "No schema" is remembered** as a choice, so auto-matching does not bring the schema
  straight back the next time the file opens. Today that pick forgets the binding.
- **An auto-match is not remembered.** It is worked out again on every open, so it improves as
  the catalog and the matcher do. Once the user picks a type within it, or picks a schema, that
  choice is remembered as today.
- **A document with no path auto-matches too** - a paste, a download. It needs no path, and
  nothing about it is remembered.
- **The button says when a schema was matched rather than chosen**, so a schema appearing on its
  own does not look like a bug.

## The rule

For each catalog schema, its best type is `JsonSchemaRootMatcher.Best(Rank(schema, keys))`, which
already declines when two types inside one schema tie. That type may be auto-bound when:

1. **No required name is missing.** Every name the type lists in `required` is among the sampled
   keys. A type with no `required` passes.
2. **And there is enough evidence**, either way:
   - *broad:* at least 6 shared names, and coverage at least 0.7 or precision at least 0.95; or
   - *small and complete:* the type requires at least 2 names, all present, and declares every
     sampled key (coverage 1.0) - `{"type": …, "features": […]}` against GeoJSON's
     FeatureCollection.
3. **And no other schema is close.** The best qualifying candidate across schemas is declined if
   another schema's best type is plausible (`JsonSchemaRootMatcher.IsPlausible`) and within
   `AmbiguityMargin` of it on both coverage and precision.

Ties between candidates that qualify are ordered as `Rank` orders them: coverage, then precision.

Only an object root is matched. An array document's first element can be ranked in the picker,
but a type cannot yet be bound as an array root's items (roadmap: "An array root bound as the
array's items"), so binding it as the root would label the wrong level. NDJSON matches its first
line, which is an object.

The bundled GeoJSON schema gains RFC 7946's `required` (`type` and `features` on the
FeatureCollection root, `type`, `geometry` and `properties` on a feature, `type` on a geometry);
without it, its three-property root could never clear the broad path.

## When it runs

- At open, once the catalog has been gathered and the first bytes have arrived (the JSON view's
  `LoadCore`, where it already samples for root ranking). Sampling is the existing bounded walk,
  inline; the fingerprints and scoring run on the background.
- Again when indexing completes, if nothing has been bound yet: a sample that stopped at a member
  still arriving (`{"meta": …, "data": [huge], "id": …}`) may have missed keys. Never once
  something is bound - a schema does not change under the user mid-session.
- Never after the user has picked anything in the flyout this session, "No schema" included.
- When the option is ticked on with nothing bound. Unticking it leaves a matched schema bound.

## Design

### Loader (`Features/Json/Schema`)

`JsonSchemaNode.RequiredKeysUtf8`, sorted like the property names. A `$ref` copies it like the
rest of the structure; `allOf` branches union into it; `oneOf`/`anyOf` branches do not - their
requirements are alternatives, and unioning them would veto documents that satisfy one branch.
That needs the branch list to remember which keyword each branch came from.
`JsonSchemaDocument.GetRequiredKeys(nodeId)` beside `GetPropertyKeys`.

### Fingerprints and the rule (`Features/Json/Schema`)

`SchemaFingerprint`: the schema's candidates (the document root when usable, then each named
root) with their sorted property and required names. Built from a parsed document and then
detached from it. `JsonSchemaRootMatcher` gains a scoring entry point over these arrays, so the
picker's ranking and the auto-match share one score. `SchemaAutoMatchRule.Choose` applies the
rule above to every schema's ranked list and returns the winner or null. Both pure and
allocation-light, as the matcher is.

### The matcher service

`ISchemaMatcher` (in `Features/Json/Schema`, beside its consumers):
`Task<SchemaAutoMatch?> FindAsync(IReadOnlyList<SchemaCatalogEntry> entries, IReadOnlyList<byte[]> documentKeys, CancellationToken documentClosing)`,
where `SchemaAutoMatch` is the entry, the type's name (null for the schema's own root) and its
score. The implementation owns the fingerprint cache and its threading - it parses on the thread
pool and is safe to call from any number of documents at once. Built in the composition root and
handed to the JSON and NDJSON view models through `DocumentViewCatalog`, as the catalog is.

### Settings

- `JsonViewSettings.OpenMatchedSchemas { get; set; } = true` - settable, so a settings file
  written before it existed reads as on.
- `SchemaBinding.SchemaPath` becomes nullable: null records "No schema" chosen for this document.
  `GatherForDocument` reports it as opted out, with nothing preselected. Old files read
  unchanged.

### Schema settings and the view models

`JsonSchemaSettings` gains `IsMatched` and `SelectMatchedAsync(entry, rootName)`. A matched
selection binds like any other but is not persisted: the view models skip `Remember` while
`IsMatched`, and picking a type or a schema clears it. `SelectAsync(null)` from the flyout
remembers "No schema".

The JSON view model samples at `LoadCore` and at `OnIndexingCompleted` and asks the matcher when
the option is on, nothing is bound, the document did not opt out and the user has not chosen
this session. The NDJSON view model samples the first line: up to 64 KB of it through a
`Utf8JsonReader` that is told the block is not final, collecting top-level property names - a
small sampler beside `JsonDocumentKeySampler`, since no line tree exists yet.

### Flyout

A check box under the schema list, "Open matched schemas", bound through the toolbar view model
to the setting. The button reads `geojson · matched` (and the type, when the schema offers one)
while the binding is a match; its tooltip says it was matched by property names and how to
change it. Look settled from a screenshot.

## Steps

1. [x] Loader reads `required`; `GetRequiredKeys`; GeoJSON schema gains `required`. Tests:
   `required` read, `$ref` copies it, `allOf` unions, `oneOf` does not.
2. [x] Fingerprints, the shared score and `SchemaAutoMatchRule`. Tests for each path of the rule,
   the required veto, cross-schema ambiguity, Keepa and GeoJSON samples matching their bundled
   schemas, and a small `{id, name}` document matching nothing.
3. [ ] `ISchemaMatcher` and its cached implementation, composed in the root. Tests: a schema
   parsed once across calls, re-parsed after it changes, a broken schema skipped.
4. [ ] Settings: the option, and the "No schema" binding. Tests with a sparse settings file and an
   old bindings file.
5. [ ] `JsonSchemaSettings.IsMatched`; the JSON view model's two attempts; remembering rules.
   Tests: matched at open, not remembered, a remembered binding wins, "No schema" sticks across
   reopen, the user's pick this session stops a late match, the option off does nothing.
6. [ ] NDJSON first-line sampler and the NDJSON view model. Tests: keys from a line longer than
   the sample window, a non-object line matching nothing.
7. [ ] Flyout check box and "matched" button text; turning the option on matches now.
   Screenshot for the look.
8. [ ] Architecture doc and roadmap updated; benchmark the first open against a catalog of 50
   schemas, including a large OpenAPI document.

## Not in this plan

- Matching one level down (`{"data": {…}}` envelopes) - the roadmap's "binding a schema type at
  a row".
- Validating a document against its schema, `required` included.
- A fingerprint cache on disk.
- XML and XSD.
