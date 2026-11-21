# Struct decoder

Goal: select bytes in the hex view, paste a C/C++ or C# struct definition, and see those bytes
read as that struct, shown the way the JSON view shows a document: `name: value` rows, nested
structs and arrays expanding in place. ("Tree" below means that view - the JSON view is drawn by
`Ui/Tree`'s `TreeSurface`, and this reuses it.) It is built
on the hex view (docs/hex-view-plan.md) and needs its caret, selection and `ByteReading` (steps 3
and 6b) first; it is that plan's step 7.

Status: planned for 2.5, all steps in one release; steps 1-5 built.

## Decided

- **The definition language is a subset of C/C++ and C#, not a template language.** Pasting the
  header or the C# type that wrote the data is the point; anything a struct definition cannot say
  (an array whose length is an earlier field, a chunk list running to the end of the file) is out
  of scope here and belongs with structure templates.
- **The struct is laid out, then read.** Parsing produces declarations; a layout pass turns them
  into fields with offsets and sizes under the language's alignment rules; decoding reads each
  field's bytes only when its row is drawn. Layout never touches the document.
- **It follows the caret, as the Values tab does.** The struct is read from the selection start
  (the caret when nothing is selected) and covers `sizeof(struct)` bytes. A selection at least two
  structs long reads as an array of `floor(length / sizeof)` of them, without asking.
- **A pin holds it in place.** Pinning fixes the struct where it is; the caret then moves inside
  it, and the field holding the caret is highlighted in the tree and outlined in the dump.
  Unpinning goes back to following the caret.
- **The byte order is the Values tab's.** One LE/BE switch, shared by both tabs and remembered,
  so the two never disagree about the same bytes. Neither language says the order in the
  definition.
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

The tree is `Ui/Tree`'s `TreeSurface` over a struct row source. The surface reads rows through
`ITreeRowSource`, not a document - `JsonDiffTree` is the precedent for a source that is not a
`TreeDocument` - so the struct tree gets selection, keyboard, find and the path bar with it.
Row keys cannot be document offsets (a struct and its first field share one, a union's members
all do), so the source numbers its rows in a space of its own: a field's index in the layout,
and an array element's arithmetically from its array's.

### Making the storage visible

The hard part of this view is not decoding but knowing how each language stores a type: is a
C# `bool` a bit or an `int`? (Neither - a byte, 0 false and anything else true, unless marshalled
as a 4-byte `BOOL`; bits only come from C bitfields.) So the layout is shown, never left to be
inferred:

- Every row shows its **offset** and type (`+8  Enabled  bool  true`), and the padding the
  layout adds shows as its own muted rows, so the offsets visibly add up to `sizeof`.
- Hovering a row names how its type is stored for the language and target in use, and its size
  and place (`StructStorageNotes`): "`bool` (C#): 1 byte, 0 = false, otherwise true - not a bit,
  and not the 4 bytes of `[MarshalAs(UnmanagedType.Bool)]`", "`long` (C, LLP64): 4 bytes,
  signed". Sizes live there rather than on the row, to keep the row to what is read.
- The selector shows the struct's total size and alignment (`268 bytes · align 4 · at 0x0`).

The sample (below) is written to show these: its layout has trailing padding, and its comments
say where each field sits.

### The sample

The editor opens empty, with **C# sample** and **C sample** buttons centred in it that fill it
with a small example in that language (its language is then detected, as a paste's is) - an `int`, a `float`, a `bool` and a 256-character array - commented with each
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

- The side panel has three tabs: **Values | Structures | Struct**.
- The **Structures tab** is where definitions are pasted and edited: the editor, its parse
  errors, Insert sample, Save, and the saved and recent definitions. A panel is too narrow to
  hold an editor and a tree of any size together, so they get a tab each.
- The **Struct tab** is the result: the selector and the tree. The selector is one strip above
  the tree: the struct's name, which opens a menu of the declared structs; its size, alignment
  and place; the target as a chip (C only), which opens a menu of targets; and the pin. With no
  definition yet the tab says so and points to the Structures tab.
- **The outline**: the mapped bytes - one struct or the whole array - are outlined in the dump
  in the caret's style. No bands per field, to keep to the colour budget; field boundaries show
  through the field outline below.
- **The field outline**: hovering a row, or (pinned) moving the caret, outlines that field's
  bytes inside the struct's. Selecting a row selects its bytes and reveals them; with the
  struct pinned, moving the caret selects the deepest field holding it. `HexSurface` finds the
  field from the sorted field list by binary search.
- "Copy as JSON" copies the decoded struct (or the expanded part of an array of them).

### Keeping definitions

A pasted definition is used at once and kept only when saved: Save writes it to a definitions
folder, as recipes and schemas are kept - a sandbox-aware folder, read through an injected store,
never by static file access - so throwaway pastes never fill it. The Structures tab lists the
saved definitions and the recently used ones; the Struct tab's picker lists every type in the
definition in use. The struct picked for a document is not remembered - the same file may hold
many structs.

## Steps

1. [x] Parser and layout for the C and C# subsets, without bitfields, unions, enums or packing.
   Tests: C# layouts checked against the runtime (`ldflda` offsets and `Unsafe.SizeOf`, or
   `Marshal.OffsetOf` for `[MarshalAs]` fields, on `Fixtures/CSharpLayouts.cs`, which the tests
   both compile and parse); C layouts against `Fixtures/c-layouts.h`, recorded from Clang's
   `-fdump-record-layouts` for both LP64 and LLP64 by `record-c-layouts.sh` and checked in.
2. [x] Packing, explicit layout, unions, enums, bitfields, inheritance, the target setting.
   Tests: the same two sources of truth, plus MSVC's mixed-type bitfield rule from fixtures.
   GCC and Clang reuse a non-POD base's tail padding and MSVC does not, so a struct records
   whether it is plain data. `struct … } __attribute__((packed));` packs, as C compilers and GCC
   do, though Clang ignores it in C++ - so that form is a unit test, not a Clang fixture.
3. [x] The tree: the struct row source, decoding rows, arithmetic arrays, padding rows, strings,
   enum names. Tests: a struct at an unaligned offset, an array of structs over a selection,
   fields past the end, both byte orders. A row's key is its place in the fully expanded tree
   (`StructNodes`), so positions are arithmetic; the scroll range is the mapping's bytes. The
   offset gutters come with the tab (step 4). When the mapping follows the caret only the tree's
   start moves - rows, keys and expansion are the same - so the surface lays its rows out again
   (`InvalidateRows`) and keeps its place; a new tree is made only when the rows change (another
   type, target or byte order, or an array of another length).
4. [x] The side panel's tabs: Structures (editor, errors) and Struct (type picker, target, the
   pin, following the caret, arrays from a long selection), byte order shared with Values.
   Visual pass from screenshots in both themes.
   Look chosen by the user: the pickers and pin on one line; a title bar above the tree (struct
   name, then size, alignment and place); the root is not a row; rows in aligned name / type /
   value columns; an offset column. A paste over the whole definition re-detects its language.
5. [x] The empty editor's sample buttons (C# and C), and making the storage visible: the
   per-type storage notes on hover, with each row's size. Tests: both samples lay out to 268
   bytes with `Name` at 9; a `[MarshalAs(UnmanagedType.Bool)]` field is 4 bytes; `fixed
   char[256]` is 512; the notes for each kind of type.
   Look chosen by the user: sizes in the hover note only, not on the row; the sample buttons in
   the empty editor; the type picker, title bar and pin combined into one selector strip.
6. [ ] The outline in the dump, the field outline, and linked selection.
7. [ ] Save, the definitions folder and recent definitions, and Copy as JSON.
8. [ ] Architecture doc, roadmap.

## Not in this plan

- Variable-length data: arrays sized by an earlier field, NUL-terminated strings of unknown
  length, offsets followed to another part of the file. That is a template language.
- Following pointers.
- C++ templates, virtual inheritance, `#include`.
- Editing a field's value.
