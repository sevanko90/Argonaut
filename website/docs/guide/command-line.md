# Command line

```
Argonaut [FILE [FILE2]] [--lines RANGE | --bytes RANGE | --lines --filter FILE | --bytes --filter FILE] [--view VIEW]
```

Give one file to open it, or two to [compare them](../features/diff.md) when both are JSON.
`--help` (or `-h`, and `/?` on Windows) prints this reference; the About box has it too, under
**Command-line options**.

## Opening part of a file

`--lines` and `--bytes` open just part of a file as a document of its own. Memory and indexing
time follow the size of the part, not the file, so a slice of a multi-gigabyte file is ready
straight away.

`--lines RANGE`
:   Those lines, numbered from 1. RANGE is `A-B`, `A-` (to the end), `A+COUNT`, or a single line
    `A`. Numbers can use `k` and `m`: `1m-2m`, `2.5m+500k`.

`--bytes RANGE`
:   Those bytes - for files whose lines are too long to pick by line, such as minified JSON. Bytes
    count from 0 and the last one is included, so `0-499` is the first 500. RANGE is `A-B`, `A-`
    or `A+COUNT`; numbers can use `k`, `m` or `g`, or be hex: `0x1F400`. A start inside a UTF-8
    character moves back to where the character starts.

A pill beside the file name in the title bar shows the range. From it you can switch between the
file's own line numbers and numbering from 1, or open the whole file.

## Filtering lines or bytes

Use `--filter FILE` with `--lines` or `--bytes`, in place of a range. Every range listed in FILE
opens together as one read-only view, in file order. Ranges are written as above, separated by
commas or new lines, and `#` starts a comment. The view follows FILE as you edit it.

```
# picks.txt
1-100
5000+20, 1m+10
```

## Choosing the view

`--view VIEW` opens the file in `json`, `ndjson`, `xml`, `csv`, `tsv` or `raw` (text) instead of
the view Argonaut would choose.

## Examples

```
Argonaut big.ndjson --lines 1m-2m
Argonaut export.csv --lines 500k+1000 --view raw
Argonaut minified.json --bytes 1.5g+100m
Argonaut big.ndjson --lines --filter picks.txt
Argonaut before.json after.json
```
