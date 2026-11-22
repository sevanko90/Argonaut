# Struct decoder

Paste the C, C++ or C# struct that wrote some binary data, and the [hex view](../views/hex.md)
reads the bytes at the caret as that struct: every field's name, type and value in a tree that
nests the way the struct does. No template language to learn. The definition you already have is
the template.

## Pasting a definition

Open the **Structures** tab in the hex view's side panel and paste a definition into the editor.
Argonaut detects whether it's C/C++ or C#, and you can change the language from the list above the
editor. If you don't have a definition to hand, **C# sample** and **C sample** give you one to
start from.

A paste can hold several types. Struct definitions, enums, `#define` and `const` sizes, and nested
types all work together. Method bodies, properties and anything else that isn't part of the
layout are skipped. If something can't be read, the editor shows the line and column, and what to
write instead.

What it understands:

C and C++
:   `struct`, `typedef struct`, `union`, anonymous nested structs and unions, `enum` and
    `enum class`, arrays of any number of dimensions, bitfields, single inheritance, pointers
    (shown as an address, never followed), `<stdint.h>` types, `#pragma pack`,
    `__attribute__((packed))` and `alignas`.

C#
:   `struct` with `[StructLayout]` (`Sequential` or `Explicit`, `Pack`, `Size`), `[FieldOffset]`,
    `fixed` buffers, `[MarshalAs]` arrays and strings, enums with an underlying type, and nested
    structs.

## Reading the struct

The **Struct** tab shows the bytes at the caret read as the struct. Each row has its offset from
the start of the struct, its name, its type and its value. Nested structs and arrays expand in
place, and padding the layout adds shows as muted rows, so the offsets visibly add up.

- **It follows the caret.** Move through the dump and the struct is read again from wherever you
  are. Select at least two structs' worth of bytes and it reads them as an array.
- **The bar at the top** chooses which of the pasted structs to read, and shows its size and
  where it starts.
- **Pin** holds the struct in place. The caret can then move inside it, and the tree follows to
  the field under the caret.
- **The struct is outlined in the dump**, so you can see what it covers. Hover a row to mark its
  bytes, or click it to select them.
- **Byte order** is the Values tab's LE/BE switch, so the two tabs always agree about the same
  bytes.
- Enums show their names, combined for flags. A `char` array shows as text up to its first NUL.
  A `byte` array that holds printable text shows as text too, and **as items** / **as text**
  switches between the two. Fields past the end of the file show `-`.

## How each type is stored

Most layout surprises come from how a language stores a type. Is a C# `bool` 1 byte or 4? Is
`long` 4 or 8? Hover a row to find out how its type is stored in the language and on the platform
in use, along with its size and place.

C leaves some sizes to the platform. The chip beside the struct's name picks the **target**:
**LP64** (macOS, Linux) or **LLP64** (Windows). The target decides the size of `long` and
`wchar_t` and how bitfields share storage. It starts as the platform you're running on, and it's
the first thing to check when a layout looks off.

!!! tip "C# `char` is two bytes"
    A C# `char` is UTF-16, so `fixed char Name[32]` takes 64 bytes where C's `char name[32]`
    takes 32. When a C# type mirrors a C header, the C# side usually wants `byte` or
    `[MarshalAs(UnmanagedType.ByValTStr)]` instead.

## Keeping definitions

- **Save** (the disk icon) saves the definition to your definitions folder, named after its
  struct.
- **Open** (the folder icon) lists your saved definitions and the ones you pasted recently. From
  its menu you can also delete a saved definition or open the definitions folder. Definitions are
  plain `.h` and `.cs` files, so you can add or edit them there.

## Copying as JSON

The copy button in the bar copies the whole struct as JSON. Right-click a row to copy just that
field or array. Field names become keys, nested structs become objects, arrays become arrays,
text arrays become strings and enums become their names. A very large array is cut off at a
size the clipboard can hold, and Argonaut tells you when that happens.

## What it doesn't do

A struct definition can't describe data whose shape depends on itself: an array whose length is
stored in an earlier field, a string that runs to a NUL, or an offset to follow somewhere else in
the file. Those need a structure template, which is on the roadmap.
