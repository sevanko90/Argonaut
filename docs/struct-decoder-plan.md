# Struct decoder

Goal: select bytes in the hex view, paste a C/C++ or C# struct definition, and see those bytes
read as that struct, shown the way the JSON view shows a document: `name: value` rows, nested
structs and arrays expanding in place. ("Tree" below means that view - the JSON view is drawn by
`Ui/Tree`'s `TreeSurface`, and this reuses it.) It is built
on the hex view (docs/hex-view-plan.md) and needs its caret, selection and `ByteReading` (steps 3
and 6) first.

Status: proposed, nothing built.

## Decided

- **The definition language is a subset of C/C++ and C#, not a template language.** Pasting the
  header or the C# type that wrote the data is the point; anything a struct definition cannot say
  (an array whose length is an earlier field, a chunk list running to the end of the file) is out
  of scope here and belongs with structure templates.
- **The struct is laid out, then read.** Parsing produces declarations; a layout pass turns them
  into fields with offsets and sizes under the language's alignment rules; decoding reads each
  field's bytes only when its row is drawn. Layout never touches the document.
- **It starts at the selection start** (the caret when nothing is selected) and covers
  `sizeof(struct)` bytes. A selection longer than one struct offers to read it as an array of
  `floor(length / sizeof)` of them.
- **Endianness is a switch on the Struct tab**, little-endian by default. Neither language says
  it in the definition, and unlike the Values tab a struct cannot show both orders at once.
- **Selecting in the character column or in another view works the same.** Raw and JSON reach
  the hex view's selection through `IByteRangeNavigable`, so "decode as struct" from them is a
  switch to Hex with the bytes already selected.

## Design

### Parsing (`Features/Hex/Structs`)

A hand-written tokenizer and recursive-descent parser for the declarations only; method bodies,
properties, constructors and anything else inside a type are skipped by brace matching. Comments
and unknown preprocessor lines are ignored. The language is detected (attributes, access
modifiers, `fixed` mean C#) and can be overridden.

C and C++:

- `struct`, `typedef struct { … } Name;`, `union`, anonymous nested structs and unions.
- `enum` and `enum class Name : type`, shown by name.
- Fields `type a, b;`, arrays `type a[N][M]`, sizes from literals, `#define N 16` and
  `constexpr`/`const` integers.
- Types: `char` (signed or unsigned by the target), `signed`/`unsigned` with `char`, `short`,
  `int`, `long`, `long long`; `float`, `double`, `bool`, `wchar_t`, `char16_t`, `char32_t`,
  `<stdint.h>` types, `size_t`, pointers (shown as an address, never followed).
- `#pragma pack(n)`, `pack(push, n)`/`pack(pop)`, `__attribute__((packed))`, `alignas(n)`.
- Bitfields: consecutive bitfields of the same type share a storage unit, low bits first - the
  case GCC, Clang and MSVC agree on. Mixed-type runs use the target's rule (below).
- Single inheritance: the base's fields first. Virtual members (a vtable pointer) are refused
  with a message rather than guessed.

C#:

- `struct` with `[StructLayout(LayoutKind.Sequential | Explicit, Pack = n, Size = n)]` and
  `[FieldOffset(n)]`.
- `fixed T name[N]`, `[MarshalAs(UnmanagedType.ByValArray, SizeConst = N)] T[]` and
  `[MarshalAs(UnmanagedType.ByValTStr, SizeConst = N)] string`.
- `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `Half`,
  `char` (UTF-16), `bool`, `nint`/`nuint`/`IntPtr`, enums with an underlying type, nested structs.
- `bool` is 1 byte, its in-memory size, not the 4 bytes of the marshaller's default. A
  `[MarshalAs(UnmanagedType.Bool)]` field is 4 bytes (Win32 `BOOL`), `UnmanagedType.U1` 1 byte.
- `char` is 2 bytes (UTF-16), so `fixed char Name[256]` is 512 bytes where C's `char name[256]`
  is 256 - the commonest surprise when a C# type mirrors a C header.

### Layout

Natural alignment: a field aligns to `min(its alignment, pack)`, a struct to its widest member,
with trailing padding to that alignment; an array's stride is its element's size. The result is
a flat list of `StructField` (path, offset, size, type, bit range) plus the padding gaps, which
the tree shows as muted `padding (3 bytes)` rows so the offsets add up.

A **target** setting decides what C leaves open: `long` and `wchar_t` (LP64 - macOS, Linux - or
LLP64 - Windows), pointer size (8 or 4), plain `char`'s signedness, and the mixed-type bitfield
rule (GCC/Clang or MSVC). It defaults to the running OS and is shown beside the struct name,
because a wrong target is the usual reason a layout is off.

Parse and layout errors are shown in the definition editor with their line and column; an unknown
type names itself, and a struct that uses one is not laid out.

### Decoding and the tree

Each row reads its field's bytes with `CopyTo` into a `stackalloc` buffer and formats them with
the hex view's `ByteReading` - the same readings as the Values tab, so a field and the
tab never disagree. Rows read `name: type = value`, with gutters for the offset relative to the
struct and the absolute offset.

- Enums show the name and the number (`Mode.Read (1)`); a `[Flags]` enum, or a C enum whose
  values are powers of two, shows the names combined.
- A `char`/`wchar_t`/`char16_t` array shows as a string up to its first NUL, expandable to its
  elements.
- **Arrays are arithmetic**: element `i` is at `base + i × stride`, so an array of a million
  structs costs nothing until it is expanded and scrolled, and nothing is materialised beyond
  the rows on screen - the same as every other tree.
- Fields past the end of the document show `-`.

Check first: whether `Ui/Tree`'s `TreeSurface` can carry this. Its rows are positioned by byte
offset, which a field has, but `TreeDocument` is built on a `SparseContainerIndex` and an
`ITreeFormatReader` over text. If an arithmetic `ITreeFormatReader` over the layout fits, the
struct tree gets selection, keyboard, find and the path bar for free; if it does not, a small
tree of its own, drawn as a `RowSurface` and styled to match the JSON view, is the fallback.

### Making the storage visible

The hard part of this view is not decoding but knowing how each language stores a type: is a
C# `bool` a bit or an `int`? (Neither - a byte, 0 false and anything else true, unless marshalled
as a 4-byte `BOOL`; bits only come from C bitfields.) So the layout is shown, never left to be
inferred:

- Every row shows its type's **size and offset** (`Enabled: bool · 1 byte @ 8 = true`), and the
  padding the layout adds shows as its own muted rows, so the offsets visibly add up to
  `sizeof`.
- Hovering a type names how it is stored for the language and target in use, from one table
  beside the layout rules: "`bool` (C#): 1 byte, 0 = false, otherwise true; 4 bytes with
  `[MarshalAs(UnmanagedType.Bool)]`", "`long` (C, LLP64): 4 bytes, signed".
- The struct's line shows its total size and alignment (`Sample · 268 bytes, align 4`).

The sample (below) is written to show these: its layout has trailing padding, and its comments
say where each field sits.

### The sample

The editor opens empty, with an **Insert sample** button that fills it with a small example in the
chosen language - an `int`, a `float`, a `bool` and a 256-character array - commented with each
field's offset and size:

```csharp
[StructLayout(LayoutKind.Sequential)]
unsafe struct Sample
{
    public int Count;            // offset 0, 4 bytes
    public float Ratio;          // offset 4, 4 bytes
    public bool Enabled;         // offset 8, 1 byte: 0 = false, anything else true
    public fixed byte Name[256]; // offset 9, 256 bytes (a C# char is 2 bytes - byte keeps it 256)
}                                // 265 bytes of fields + 3 of padding = 268, align 4
```

```c
struct Sample {
    int32_t count;   /* offset 0, 4 bytes */
    float   ratio;   /* offset 4, 4 bytes */
    bool    enabled; /* offset 8, 1 byte */
    char    name[256]; /* offset 9, 256 bytes */
};                   /* 265 bytes of fields + 3 of padding = 268, align 4 */
```

The two have the same layout, so switching language on the sample changes nothing in the tree -
which is itself a check. The sample is never saved into the definitions folder unless edited.

### In the hex view

- The **Struct tab** of the hex view's side panel, right of the character column: the definition
  editor above (collapsible once a definition is in), a picker of the types it declares, the
  endianness and target switches, and the tree below. The panel's layout inside the tab is
  chosen from screenshots.
- **Field bands**: the mapped range is tinted in the grid, alternating per top-level field, so
  the struct's shape shows in the dump. `HexSurface` draws them from the sorted field list,
  finding the first visible field by binary search.
- **Linked selection**: selecting a row selects its bytes in the grid and reveals them; moving
  the caret in the grid selects the deepest field holding it.
- "Copy as JSON" copies the decoded struct (or the expanded part of an array of them).

### Keeping definitions

Pasted definitions are kept as files in a definitions folder, as recipes and schemas are: a
sandbox-aware folder, read through an injected store, never by static file access. The picker
lists every type in every file there, so a header pasted once stays available. The struct picked
for a document is not remembered - the same file may hold many structs.

## Steps

1. [ ] Parser and layout for the C and C# subsets, without bitfields, unions, enums or packing.
   Tests: C# layouts checked against the runtime (`Unsafe.ByteOffset` on an instance of the same
   struct, compiled into the test); C layouts against fixtures recorded once from Clang's
   `offsetof` for both LP64 and LLP64, checked in.
2. [ ] Packing, explicit layout, unions, enums, bitfields, inheritance, the target setting.
   Tests: the same two sources of truth, plus MSVC's mixed-type bitfield rule from fixtures.
3. [ ] The tree: the `TreeSurface` check, then decoding rows, arithmetic arrays, padding rows,
   strings, enum names. Tests: a struct at an unaligned offset, an array of structs over a
   selection, fields past the end, both endiannesses.
4. [ ] The Struct tab in the hex view's side panel: editor, type picker, errors, mapping from the selection.
   Visual pass from screenshots in both themes.
5. [ ] The empty editor's Insert sample button (C# and C), and making the storage visible: size
   and offset on every row, padding rows, the struct's size and alignment, the per-type storage
   notes on hover. Tests: both samples lay out to 268 bytes with `Name` at 9; a
   `[MarshalAs(UnmanagedType.Bool)]` field is 4 bytes; `fixed char[256]` is 512.
6. [ ] Field bands and linked selection.
7. [ ] The definitions folder, and Copy as JSON.
8. [ ] Architecture doc, roadmap.

## Not in this plan

- Variable-length data: arrays sized by an earlier field, NUL-terminated strings of unknown
  length, offsets followed to another part of the file. That is a template language.
- Following pointers.
- C++ templates, virtual inheritance, `#include`.
- Editing a field's value.
