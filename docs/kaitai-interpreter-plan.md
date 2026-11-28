# Kaitai Struct formats in the hex view

Goal: read any binary format a Kaitai Struct `.ksy` file describes - ZIP, ISO, MKV, a game's save
files - and show it in the hex view's struct panel, lazily, over files of many gigabytes. This is
the "structure templates" item on the roadmap: what a C or C# struct definition cannot say (an
array sized by an earlier field, a string ending at a NUL, a chunk list running to the end of the
file, an offset followed elsewhere).

The reading is done by **Ksyrt**, a Kaitai Struct runtime in a repository and NuGet package of its
own, with no reference to Argonaut or Avalonia. It interprets a `.ksy` at run time rather than
generating code, reads only what is asked for, and reads through `IBinaryInput` - whose members
are exactly `IByteSource`'s reading members, with the same contracts. Its design, testing and
steps are in its own `docs/plan.md`. This plan is the Argonaut side: driving the hex view's struct
panel with it, first with the C and C# structs the panel already decodes, then with `.ksy` files,
then the UX that general formats need, in iterations.

Status: planned. Ksyrt's repository holds its skeleton and the input contract.

What Argonaut relies on from Ksyrt, so this plan can be read alone:

- **A lazy tree.** Nodes with a name, kind, type text, range (bit-precise for bit fields), value
  decoded on demand, enum name, `doc` and `-` keys; children resolved only when asked for;
  arrays indexable; a stable identity per node.
- **Budgets.** Every query takes a budget and comes back ready, failed, incomplete (the input has
  not arrived) or pending; `Settle(node, stopping)` does pending work on a background thread and
  publishes it lock-free.
- **It never blocks and never throws out of the tree.** Waiting for a growing source, and
  scheduling, are the host's.
- **Specs built in code**, and written back out as YAML.
- **Hooks**: an import resolver, a store for large decoded buffers, custom processors.

---

# Argonaut

## Decided

- **One decoding engine.** Once Ksyrt can decode the C and C# structs the panel already
  handles, it decodes them, and the panel's own decoding goes. The C and C# parsers and the layout
  pass stay: they are how a pasted header becomes a schema.
- **The first step changes nothing a user can see.** It proves Ksyrt inside the app
  against the existing decoder, which is the best oracle available: years of tested layouts.
- **The UI is built in iterations** after that, each one usable.

## Steps

### 2.1 The reference

`Argonaut` references the `Ksyrt` package (a local package feed while both are moving, the
published package once Ksyrt releases). `IByteSource` extends `IBinaryInput`. `ArchitectureTests`
gains a rule that Ksyrt's namespaces are used only in `Features/Hex` and through `IBinaryInput`
in `Engine/Bytes`, so the app could change runtimes without reaching every layer.

### 2.2 C and C# structs through Ksyrt (no visible change)

A translator in `Features/Hex/Structs` turns a laid-out struct (`LaidStruct`) into a code-built
schema. The layout pass has already decided every offset, so the translation is mechanical and
never re-derives alignment:

| Struct decoder | Kaitai schema |
|---|---|
| struct | a type, read with `size: sizeof`, so it is its own sub-stream |
| field at an offset | a `seq` attribute; padding becomes a `size: n` attribute marked `-argonaut-padding` |
| union member, `[FieldOffset]` | a positioned instance, `pos: offset`, inside the struct's sub-stream |
| integers, floats | `u`/`s`/`f` types in the Values tab's byte order |
| bitfield | the storage unit read once, each field a value instance `(unit >> offset) & mask` - exact for both the GCC and MSVC rules, which the layout pass has already applied |
| enum | an enum, `[Flags]` marked with a `-` key |
| `char` array | `strz` with `size: n` and the language's encoding, and the elements |
| `bool`, pointer, `Half` | `u1`/`u4`, `u4`/`u8`, `u2`, with a `-argonaut-format` key saying how to show it |
| array of N | `repeat: expr`, `repeat-expr: N`, whose fixed element size makes it arithmetic |

The panel then reads values from Ksyrt's nodes and formats them with the existing
`StructValueText` and `ByteReading`, so a field and the Values tab still never disagree.

**Row keys.** `StructTree` keys a row by its position in the fully expanded tree, which works out
by arithmetic only because every struct has a fixed size. A general `.ksy` breaks that: a
variable-length array's row count is not known until it is walked. So the tree source is rebuilt
here over Ksyrt's node identities, with the scroll range estimated, the way
`TreeDocument` already estimates over a document. Arithmetic positions remain the fast path for
subtrees of fixed size, which is every one in this step.

**Tests**: every existing struct-decoder test still passes, and a parity test decodes each struct
fixture both ways - the old `StructNodes` path and Ksyrt - and compares every row's
label, type, offset, size and value text. The old decoding path is removed when that test passes
over every fixture.

### 2.3 Opening a `.ksy`

Kaitai becomes the panel's third definition language beside C and C#: paste or open a `.ksy`, and
it is detected (`meta:` and `seq:` at the top level) as a C# definition is.

**Two placements**, because a spec describes either a whole file or a part of one:

- **A whole file, pinned at byte 0.** The default for a `.ksy`. The root is the document's start
  and the tree is the file's structure; the caret moving does not move the root.
- **A part of a file, applied at the caret.** Unpinning makes the spec follow the caret as a C
  struct does: the root is the caret, which is how a format embedded in another file (a header
  inside a container, a record found by search) is read. A known start means no walk is needed to
  reach it.

**Following the caret in a whole-file spec.** The panel stays rooted at the file's root, and as
the caret moves it asks Ksyrt which node holds that byte (`tree.Locate`): the ancestors of that
node are expanded and it is scrolled into view - `header › records › [48,211] › payload` - rather
than the user opening a million-element array by hand. A caret past where the structure has been
indexed shows "Indexing structure… 34%" in the panel and fills in when the walk arrives, as the
raw view does for a line past its index.

- **Problems** show in the definition editor at their line and column, as C and C# problems do.
  An unsupported feature names itself.
- **Imports** resolve against the definition's folder, and against the struct definition folder
  (`StructDefinitionFolderStore`), through Ksyrt's import resolver.
- **A format that must be read from the start** - one whose big repeat is found only by parsing
  every element (Ksyrt classifies each repeat from the spec) - says so when it is applied, so a
  slow jump ahead is expected rather than a surprise.

### 2.4 Background work

Ksyrt produces the structure's index; Argonaut runs it. Ksyrt has no threads: it offers a
`StructureWalk` that does one bounded slice per `Advance` call on whatever thread calls it, and
keeps the checkpoints in its own graph. Everything about running a scan is Argonaut's, and already
exists, so none of it is duplicated:

- **A structure index is one more `IBackgroundIndex`.** Its scan body drives the walk -
  `Advance(4 MB, stopping)` in a loop, progress through the existing reporter, and on `Incomplete`
  the source's `WaitForLength` before carrying on (blocking stays Argonaut's, on the background
  thread, as CLAUDE.md requires). It records nothing itself: the checkpoints are Ksyrt's.
- **It starts when a whole-file spec is applied**, not when the caret jumps - as a file's lines
  are indexed on open - so by the time the user scrolls halfway the walk has usually passed it. A
  size-only walk runs at about the input's read speed.
- **It is a new background reader of the document's file**, so it joins the document session's
  cancel → join → release order and the save's stop-and-join list (`RawViewModel.SaveAsync`, see
  CLAUDE.md). Missing that fails saves on Windows only.
- **It is kept across view switches** through `KeptIndexes`: the finished checkpoints
  (`tree.DetachIndex()`) are kept under the origin and a hash of the `.ksy` text, checked against
  the origin's `ByteOriginVersion` like every kept index, and handed back to `spec.Open`. Leaving
  Hex and coming back, or reapplying the same spec, does not walk again.
- **Rows are drawn with a small budget.** A pending value shows `…` and its work goes to a
  background `Settle`; when it publishes, the row redraws through `RowSurface.OutsideRenderPass`,
  never from inside the render pass.
- **A `repeat-until` or `repeat: eos`** shows its count as "at least n" while it is walked, its
  rows available as the walk reaches them.
- **A growing source** (a download still arriving) shows the not-yet-arrived state, and the walk
  carries on as the source grows.

### 2.5 Seeing it in the dump

- The selected node's range is outlined in the hex dump, as the pinned field is now; a bit field
  outlines its bits within the byte.
- A node under a processed (decompressed) attribute outlines the raw attribute it came from, and
  says so.
- Clicking in the dump selects the deepest node holding the caret, reading only along that path.

### 2.6 Later iterations

To be planned once the above is in use:

- `doc` on hover, `-webide-representation` as the row's summary text.
- A folder of `.ksy` files, picked like struct definitions, and a link to the format gallery.
- Choosing a format automatically by extension and magic bytes, as schemas are matched.
- **Save a C or C# struct as `.ksy`** - the 2.2 translator plus a YAML writer - so a definition
  started in Argonaut can be shared, opened in the web IDE, or offered to the gallery.
- Copy as JSON in `ksdump`'s shape, beside the existing Copy as JSON.

## Open questions

- **Where a large decoded buffer spills**: a temp file beside the response folder's, with the
  origin's lifetime, or the document session's.
- **Whether a `.ksy` read from the caret** should be the default for small formats (a single
  header), decided by whether the format uses `size-eos` or `repeat: eos` at its root.
