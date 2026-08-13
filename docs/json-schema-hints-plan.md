# JSON Schema-driven display hints

## Context

Argonaut shows JSON structure from a token index only — it never holds the document in memory.
Users reading opaque payloads (fixed-layout arrays, coded enum values, terse property names)
have no way to know what a value means. A JSON Schema carries exactly that documentation in
`title` / `description`, and schemas are small enough to hold whole in RAM.

Goal: bind a schema to an open document and render its `title` inline (muted, after the value)
with `description` as a tooltip, covering object members, positional array slots (`prefixItems`),
and enum member labels.

### Why this is practical despite not holding the document

Two ways to map a document node to a schema node:

- **Bottom-up** (build each row's JSON path, resolve it against the schema): *rejected*.
  `JsonPathBuilder.FindArrayIndex` (`Argonaut/Features/Json/JsonPathBuilder.cs:80`) is
  O(preceding siblings) — catastrophic on a million-element array.
- **Top-down, in lockstep with the existing tree walk**: *chosen*.
  `JsonVisibleRowCollection.AppendSubtree` (`Argonaut/Features/Json/JsonVisibleRowCollection.cs:728`)
  already walks the visible tree from the root and already carries the array ordinal down as a
  parameter (`arrayIndex`, used for `VisibleRow.ArrayIndex`). Carrying a schema node id down the
  same recursion is O(1) per row, needs no path building, and needs no extra mmap reads beyond
  the property-key span already reachable from `JsonTokenInfo.NameOffset/NameLength`.

Cost: `VisibleRow` grows 12→16 bytes (100k-row worst case = 1.6 MB). With no schema loaded the
added work is one `int < 0` test per row. Enum-label matching happens in `BuildRow`, which only
runs for the ≤1000 LRU-cached realized rows, and reuses the value string `BuildRow` already
decodes — zero extra allocation.

## Schema model

New folder `Argonaut/Features/Json/Schema/`.

### `JsonSchemaDocument.cs`
Immutable flattened graph, all cross-references as `int` node ids (`-1` = none), so recursive
`$ref` is free and nothing needs to recurse at document-walk time.

```csharp
public sealed class JsonSchemaDocument
{
    public int RootId { get; }
    // Object members: binary search over UTF-8 keys, zero allocation.
    public int ResolveMember(int parentNodeId, ReadOnlySpan<byte> utf8Key);
    // Array elements: prefixItems[ordinal] if in range, else items.
    public int ResolveElement(int parentNodeId, int ordinal);
    public string? GetTitle(int nodeId);
    public string? GetDescription(int nodeId);
    // Enum member labels, matched against the already-decoded display text.
    public bool TryGetEnumLabel(int nodeId, string valueText, JsonTokenKind kind,
                                out string? title, out string? description);
}
```

Per-node storage (`JsonSchemaNode.cs`): `Title`, `Description`, `ItemsId`,
`AdditionalPropertiesId`, `int[] PrefixItemIds`, `byte[][] PropertyKeysUtf8` (sorted ordinal) +
parallel `int[] PropertyNodeIds`, and optional `EnumLabel[] EnumLabels`
(`record struct EnumLabel(string ValueText, string? Title, string? Description)`).

### `JsonSchemaLoader.cs`
`System.Text.Json` `JsonDocument` parse, then two passes: create nodes, then patch `$ref`
targets. Keyword coverage:

- **Core**: `title`, `description`, `properties`, `items`, `prefixItems` (plus draft-07
  array-form `items` treated as `prefixItems`), `additionalProperties`,
  `$defs` / `definitions`, and local `#/...` `$ref` (recursive refs fine — ids only).
- **`allOf`**: merged into the owning node at load time (first non-null `title`/`description`
  wins; property maps unioned, earliest branch wins on key conflict).
- **`oneOf` / `anyOf`**: two distinct shapes, both handled at load time so runtime stays O(1):
  - every branch has a `const` → becomes this node's `EnumLabels` table, no structural descent.
  - otherwise treated as structural alternatives and **merged** exactly like `allOf`.
    Deliberate simplification: no value-based discrimination at walk time. Document it.
- **Enum labels** also built from `enum` + the OpenAPI-conventional `x-enumNames` /
  `x-enumDescriptions` sibling arrays (positional).
- Remote `$ref` (any `$ref` that isn't a local `#` pointer), `patternProperties`,
  `if`/`then`/`else`, `$dynamicRef`: ignored silently.

Parse failure, unreadable file, or a file over an 8 MB cap → returns `null`, no error surfaced
(user requirement: "if nothing matches, don't complain, just show nothing"). Parsing runs on
`Task.Run` so a pathological schema can't stall the UI thread.

## Schema catalog and selection

### `JsonSchemaCatalog.cs`
Merges two sources into one list of `SchemaCatalogEntry(string DisplayName, string FilePath, bool IsUser)`:

1. **Bundled**: `*.json` under `Path.Combine(AppContext.BaseDirectory, "Schemas")`.
   Source lives at `Argonaut/Schemas/`, shipped via a `csproj` `Content` item with
   `CopyToOutputDirectory=PreserveNewest`.
2. **User**: `*.json` under a new `AppDataPaths.GetSchemasDirectory()` →
   `<AppData>/Argonaut/Schemas`. A user file with the same name shadows the bundled one.

Display name = file stem. No schema is parsed during enumeration — only on selection — so a
folder with hundreds of schemas costs one directory listing.

`EnsureUserDirectory()` creates the folder if absent and returns its path; the toolbar's
"Open schema folder…" item calls it then launches the OS file manager via
`Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })`.

### Sidecar auto-selection
On `JsonViewModel.LoadAsync(path)`, if `<path>.schema.json` exists it is added as a transient
catalog entry and pre-selected. Never an error if absent.

### `JsonSchemaSettings.cs` (in `Schema/`)
Deliberate mirror of `Argonaut/Features/Json/Hints/DateHintSettings.cs` — per-document session
state, UI-thread only, `ObservableObject`, exposing `Entries`, `SelectedEntry`,
`Document` (the loaded `JsonSchemaDocument?`), `SelectAsync(entry)` and an event
`SchemaChanged`. `JsonViewModel` owns one, created eagerly like `HintSettings`
(`JsonViewModel.cs:58`).

### `Argonaut/Infrastructure/SchemaSelectionPreference.cs`
Remembers the chosen schema per document path across sessions. Follows the existing one-file
preference pattern of `ExpandDepthPreference.cs` on top of `JsonSettingsStore` — a
`Dictionary<string,string>` of document path → schema file path, capped at 100 entries
(most-recent-first), file `schema-selection.json`.

## Wiring into the row pipeline

### `JsonVisibleRowCollection.cs`
- `VisibleRow` (`:902`) gains `int SchemaNodeId` (`-1` = none); `ForToken` takes it,
  `ForMorePlaceholder` and closing-bracket rows pass `-1`.
- `AppendSubtree(int tokenIndex, List<VisibleRow> into, int arrayIndex, int schemaNodeId)`.
  `Rebuild()` seeds it with `schema?.RootId ?? -1`. Before recursing into each child:

  ```csharp
  int childSchemaId = schemaNodeId < 0 || schema is null ? -1
      : token.Kind == JsonTokenKind.StartArray
          ? schema.ResolveElement(schemaNodeId, shown)
          : child.NameLength >= 0
              ? schema.ResolveMember(schemaNodeId, mmap.GetSpan(child.NameOffset, child.NameLength))
              : -1;
  ```

  The `schemaNodeId < 0` short-circuit means the no-schema path adds one branch per row and
  never touches the mmap. Once the schema runs out below some subtree, `-1` propagates and the
  rest of that subtree is free too.
- `BuildRow` (`:498`) resolves display text from `vrow.SchemaNodeId`: `GetTitle`/`GetDescription`,
  then `TryGetEnumLabel(nodeId, value, token.Kind, …)` using the `value` string it already
  decoded — an enum-member label, when matched, supersedes the node's own title.
  Enum matching is exact text; for `JsonTokenKind.Number` fall back to a `decimal`-normalized
  compare so `3` matches `3.0`. Document that as the known limit.
- `JsonRow` (`:17`) gains `SchemaTitle` and `SchemaDescription` (both `string?`), kept separate
  from the existing `Hint` so a row can show both a decoded date and a schema title.
- `public void SetSchema(JsonSchemaDocument? schema)` stores it and calls `Rebuild()` —
  schema ids live inside `visibleRows`, so unlike `InvalidateRealizedRows` (`:814`) this needs a
  structural rebuild. Structure itself is unchanged, so it lands on `Rebuild`'s existing
  `Reset` path.

Note: schema hints do **not** go through `IValueHintProvider`. That interface classifies by
*value bytes* (`TryClassify(kind, span)`); schema hints are keyed by *position in the tree*.
Forcing them through it would require re-deriving the path per row — the rejected bottom-up
approach.

### `JsonView.axaml`
One `TextBlock` appended after the existing `Hint` button (`:163-169`), same muted treatment,
description on the tooltip:

```xml
<TextBlock Text="{Binding SchemaTitle}"
           IsVisible="{Binding SchemaTitle, Converter={x:Static json:IsNotNullConverter.Instance}}"
           Foreground="{DynamicResource AppMutedTextBrush}"
           FontFamily="{DynamicResource AppContentFontFamily}"
           Margin="8,0,0,0"
           VerticalAlignment="Center"
           ToolTip.Tip="{Binding SchemaDescription}" />
```

Non-clickable, so a plain `TextBlock` rather than the `hintlink` `Button` style. Fixed row
height is preserved (no wrapping, no second line).

### `JsonToolbarView.axaml` / `JsonToolbarViewModel.cs`
A `ComboBox` before the expand-depth combo, bound to `SchemaEntries` / `SelectedSchemaIndex`,
with a leading "No schema" item and a trailing "Open schema folder…" item that reverts the
selection and opens the folder. The VM takes `JsonSchemaSettings` as a second constructor
argument alongside `DateHintSettings` and mirrors the existing `OnSettingsPropertyChanged` /
`SyncFromSettings` pattern (`JsonToolbarViewModel.cs:118-125`) so a sidecar auto-selection
lights up the combo.

### `JsonViewModel.cs` / `NdJsonViewModel.cs`
- `JsonViewModel`: `public JsonSchemaSettings SchemaSettings { get; } = new();` next to
  `HintSettings` (`:58`); in `LoadAsync` populate the catalog, apply sidecar or the remembered
  `SchemaSelectionPreference`; subscribe `SchemaSettings.SchemaChanged` → `rows?.SetSchema(...)`
  + `SchemaSelectionPreference.Save(...)`.
- `NdJsonViewModel`: holds the master `JsonSchemaSettings` and pushes the loaded
  `JsonSchemaDocument` into each nested per-line `JsonViewModel`, mirroring the existing
  master/child `DateHintSettings` propagation (`NdJsonViewModel.cs:121-154`). Sharing the parsed
  document (not re-parsing per line) is the point.

### `Argonaut/Schemas/` (bundled content)
Ship a small starter set. At minimum a `keepa-product.json` covering the `csv` `prefixItems`
layout, since that pairs with the existing Keepa date decoding and is the motivating case.

## Verification

Unit tests (`Argonaut.Tests/`), following the established fixtures:

- `JsonSchemaLoaderTests` — `$ref` including a recursive one, `$defs` and `definitions`,
  `allOf` merge, `oneOf` const→enum-labels vs `oneOf` structural→merge, draft-07 array `items`,
  malformed/oversized schema → `null`.
- `JsonSchemaDocumentTests` — `ResolveMember` UTF-8 binary search (hit, miss,
  `additionalProperties` fallback, non-ASCII key), `ResolveElement` in/out of `prefixItems`
  range, `TryGetEnumLabel` string/number/`3` vs `3.0`.
- `JsonSchemaRowTests` — end-to-end, copying `DateHintRowTests.cs` exactly (temp file →
  `MMapFile` → `JsonStructureIndex.StartIndexing` → `JsonVisibleRowCollection` →
  `FindVisiblePosition` → assert `((JsonRow)rows[pos]!).SchemaTitle`). Cases: object member
  title, `prefixItems` slot title on `[1]`, enum label superseding node title, deep subtree
  where the schema runs out → `SchemaTitle` null, `SetSchema(null)` clears titles and fires
  `Reset`, and a date hint plus schema title co-existing on one row.
- `JsonSchemaCatalogTests` — bundled + user merge and user-shadows-bundled, using the existing
  `AppDataPaths.RootOverride` test seam (`AppDataPaths.cs:12`) so nothing touches real settings.
- `JsonToolbarViewModelTests` — extend for schema selection and sidecar-driven combo sync.

Run: `dotnet test Argonaut.Tests`.

Manual check (needed for the visual/UI half — please verify rather than me launching the app):
open a JSON file, pick a bundled schema from the toolbar, confirm muted titles appear on
matching rows, tooltips show descriptions, row height is unchanged, and "Open schema folder…"
creates and opens `<AppData>/Argonaut/Schemas`. Then drop a `<file>.schema.json` beside a
document and confirm it auto-selects on open.

## Known limits (worth stating in code comments)

- `oneOf`/`anyOf` structural branches are merged, not discriminated against actual values.
- Enum matching is textual (with numeric normalization), not JSON-value-equality.
- Remote `$ref`, `patternProperties`, `if`/`then`/`else` are ignored.
