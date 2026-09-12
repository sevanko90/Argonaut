#!/usr/bin/env python3
"""Builds the embedded Unicode character name table from the Unicode Character Database.

The app shows the character under the caret as "U+2028 LINE SEPARATOR", and .NET carries no name
database - only categories. This turns two UCD files into one deflate-compressed resource that
Argonaut expands on first lookup (see Argonaut/Infrastructure/Unicode/UnicodeNames.cs).

Two files are needed, not one:

  UnicodeData.txt   the name of every individually listed code point. Ranges (CJK, Hangul, Tangut,
                    private use) appear only as First>/Last> markers, because their names are
                    algorithmic; they are emitted as ranges for the reader to format.
  NameAliases.txt   the real names of the C0/C1 controls, whose Name field in UnicodeData.txt is
                    literally "<control>". "U+0085 NEXT LINE" comes from here.

Run it deliberately, not from the build: the generated resource is committed, so the Unicode
version only moves when someone means it to.

    python3 scripts/make-unicode-names.py --version 16.0.0

Downloads both files unless --ucd-dir points at local copies.
"""

from __future__ import annotations

import argparse
import pathlib
import sys
import urllib.request
import zlib

UCD_URL = "https://www.unicode.org/Public/{version}/ucd/{name}"

# Range markers in UnicodeData.txt, mapped to the name prefix the code point's hex is appended to.
# "Hangul Syllable" is the exception: its names are composed from jamo, so the reader is told which
# range needs that rather than given a prefix.
RANGE_PREFIXES = {
    "CJK Ideograph": "CJK UNIFIED IDEOGRAPH-",
    "Tangut Ideograph": "TANGUT IDEOGRAPH-",
    "Non Private Use High Surrogate": "SURROGATE-",
    "Private Use High Surrogate": "SURROGATE-",
    "Low Surrogate": "SURROGATE-",
    "Private Use": "PRIVATE USE-",
    "Plane 15 Private Use": "PRIVATE USE-",
    "Plane 16 Private Use": "PRIVATE USE-",
}

HANGUL_RANGE_LABEL = "Hangul Syllable"


def fetch(version: str, name: str, ucd_dir: pathlib.Path | None) -> str:
    if ucd_dir is not None:
        return (ucd_dir / name).read_text(encoding="utf-8")

    url = UCD_URL.format(version=version, name=name)
    print(f"fetching {url}", file=sys.stderr)
    with urllib.request.urlopen(url, timeout=120) as response:
        return response.read().decode("utf-8")


def range_label(marker: str) -> str:
    """"<CJK Ideograph Extension A, First>" -> "CJK Ideograph"."""
    label = marker.strip("<>").rsplit(",", 1)[0]
    for known in (*RANGE_PREFIXES, HANGUL_RANGE_LABEL):
        if label.startswith(known):
            return known
    raise SystemExit(f"unknown range marker: {marker}")


def control_names(aliases: str) -> dict[int, str]:
    """The "control" aliases, which are the only real names the C0/C1 controls have.

    The first alias listed for a code point is its primary one (U+000A is LINE FEED, not NEW LINE
    or END OF LINE), so later ones are ignored. Abbreviations (LF, NEL) are skipped - they are
    what the Control Picture glyph already conveys.
    """
    names: dict[int, str] = {}
    for line in aliases.splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue

        code, name, kind = line.split(";")
        if kind != "control":
            continue

        names.setdefault(int(code, 16), name)

    return names


def build(unicode_data: str, aliases: str) -> tuple[list[tuple[int, str]], list[tuple[int, int, str]]]:
    """Returns (names, ranges). A range's third field is its name prefix, or "" for Hangul."""
    controls = control_names(aliases)
    names: list[tuple[int, str]] = []
    ranges: list[tuple[int, int, str]] = []
    pending_range_start: int | None = None
    pending_range_label: str | None = None

    for line in unicode_data.splitlines():
        if not line:
            continue

        fields = line.split(";")
        code = int(fields[0], 16)
        name = fields[1]

        if name.endswith(", First>"):
            pending_range_start = code
            pending_range_label = range_label(name)
            continue

        if name.endswith(", Last>"):
            if pending_range_start is None or pending_range_label is None:
                raise SystemExit(f"range end without a start: {line}")

            prefix = "" if pending_range_label == HANGUL_RANGE_LABEL \
                else RANGE_PREFIXES[pending_range_label]
            ranges.append((pending_range_start, code, prefix))

            pending_range_start = None
            pending_range_label = None
            continue

        if name == "<control>":
            if code in controls:
                names.append((code, controls[code]))
            continue

        if name.startswith("<"):
            continue

        names.append((code, name))

    names.sort()
    ranges.sort()
    return names, ranges


def pack(names: list[tuple[int, str]], ranges: list[tuple[int, int, str]], version: str) -> bytes:
    """Lays the table out so the reader can binary-search it in place.

    One allocation at runtime: the decompressed bytes. Code points and offsets are read from that
    same array with BinaryPrimitives rather than copied into int[]s, and a name string is
    materialized only for the character actually asked about.

        magic "AUNM", format version, name count, range count      (16 bytes)
        name count   x int32 code point, ascending
        name count+1 x int32 offset into the text blob
        range count  x (int32 start, int32 end, int32 prefix offset, int32 prefix end)
        text blob    UTF-8, names then range prefixes, no separators
    """
    blob = bytearray()
    offsets = [0]
    for _, name in names:
        blob += name.encode("utf-8")
        offsets.append(len(blob))

    range_records = []
    for start, end, prefix in ranges:
        prefix_start = len(blob)
        blob += prefix.encode("utf-8")
        range_records.append((start, end, prefix_start, len(blob)))

    def int32(value: int) -> bytes:
        return value.to_bytes(4, "little", signed=True)

    out = bytearray(b"AUNM")
    out += int32(1)
    out += int32(len(names))
    out += int32(len(ranges))
    for code, _ in names:
        out += int32(code)
    for offset in offsets:
        out += int32(offset)
    for start, end, prefix_start, prefix_end in range_records:
        out += int32(start) + int32(end) + int32(prefix_start) + int32(prefix_end)
    out += blob

    print(f"Unicode {version}: {len(names):,} names, {len(ranges)} ranges, "
          f"{len(blob):,} bytes of text", file=sys.stderr)
    return bytes(out)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="16.0.0", help="Unicode version to fetch")
    parser.add_argument("--ucd-dir", type=pathlib.Path,
                        help="read UnicodeData.txt and NameAliases.txt from here instead of fetching")
    parser.add_argument("--out", type=pathlib.Path,
                        default=pathlib.Path("Argonaut/Assets/Unicode/UnicodeNames.deflate"))
    args = parser.parse_args()

    unicode_data = fetch(args.version, "UnicodeData.txt", args.ucd_dir)
    aliases = fetch(args.version, "NameAliases.txt", args.ucd_dir)

    names, ranges = build(unicode_data, aliases)
    table = pack(names, ranges, args.version)

    # Raw deflate, no zlib or gzip wrapper, which is what System.IO.Compression.DeflateStream reads.
    compressor = zlib.compressobj(level=9, wbits=-15)
    packed = compressor.compress(table) + compressor.flush()

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_bytes(packed)

    print(f"-> {args.out} ({len(table):,} bytes, {len(packed):,} compressed)", file=sys.stderr)


if __name__ == "__main__":
    main()
