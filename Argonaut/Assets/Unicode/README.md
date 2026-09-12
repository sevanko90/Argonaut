# Unicode character names

`UnicodeNames.deflate` is a generated, deflate-compressed table of Unicode character names, read by
`Argonaut/Infrastructure/Unicode/UnicodeNames.cs` so the raw view's status gutter can say
"U+2028 LINE SEPARATOR" rather than just "U+2028". .NET exposes character categories but no names.

Generated from the Unicode Character Database, version **16.0.0**, by
`scripts/make-unicode-names.py` — run deliberately, not from the build, so the Unicode version only
moves when someone means it to:

    python3 scripts/make-unicode-names.py --version 16.0.0

Two UCD files go into it:

- `UnicodeData.txt` — the name of every individually listed code point. Ranges (CJK, Hangul,
  Tangut, private use) appear only as `First>`/`Last>` markers because their names are algorithmic;
  the reader composes those.
- `NameAliases.txt` — the real names of the C0/C1 controls, whose name field in `UnicodeData.txt` is
  literally `<control>`. "U+0085 NEXT LINE" comes from here.

271KB compressed on disk; inflated once on first lookup into a single ~1.3MB byte array that is
binary-searched in place, so a lookup allocates only the name it returns.

## Licence

The data is from the Unicode Character Database, © Unicode, Inc., distributed under the Unicode
License: https://www.unicode.org/license.txt

Unicode and the Unicode Logo are registered trademarks of Unicode, Inc. in the United States and
other countries.
