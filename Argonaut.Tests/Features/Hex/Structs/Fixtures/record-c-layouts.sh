#!/bin/sh
# Records Clang's layout of every struct in c-layouts.h for the two targets the struct decoder
# knows, as "<name> <size> <alignment> <field offsets...>": sizes in bytes, offsets in bits. Run it from this folder after
# changing c-layouts.h, and check the results in. Clang's own records, and a struct only a typedef
# names (Clang calls it "unnamed"), are left out - the structs that contain one check its layout.
set -e
cd "$(dirname "$0")"
prelude='typedef __INT8_TYPE__ int8_t; typedef __UINT8_TYPE__ uint8_t;
typedef __INT16_TYPE__ int16_t; typedef __UINT16_TYPE__ uint16_t;
typedef __INT32_TYPE__ int32_t; typedef __UINT32_TYPE__ uint32_t;
typedef __INT64_TYPE__ int64_t; typedef __UINT64_TYPE__ uint64_t;
typedef __SIZE_TYPE__ size_t; typedef __INTPTR_TYPE__ intptr_t;
typedef __UINTPTR_TYPE__ uintptr_t; typedef __PTRDIFF_TYPE__ ptrdiff_t;'

record() {
    { echo "$prelude"; cat c-layouts.h; } \
        | clang -target "$1" -x c++ -std=c++17 -fsyntax-only \
            -Xclang -fdump-record-layouts-simple -Xclang -fdump-record-layouts-complete - 2>/dev/null \
        | awk '
            /^Type: / { sub(/^Type: (struct|class|union) /, ""); sub(/^Type: /, ""); name = $0 }
            /^  Size:/ { split($0, a, ":"); size = a[2] / 8 }
            /^  Alignment:/ { split($0, a, ":"); align = a[2] / 8 }
            /^  FieldOffsets:/ && name !~ /^__|\(/ {
                line = $0; sub(/.*\[/, "", line); sub(/\].*/, "", line); gsub(/,/, "", line)
                n = split(line, offsets, " "); out = name " " size " " align
                for (i = 1; i <= n; i++) out = out " " offsets[i]
                print out
            }' > "$2"
}

record x86_64-apple-darwin c-layouts.lp64.txt
record x86_64-pc-windows-msvc c-layouts.llp64.txt
