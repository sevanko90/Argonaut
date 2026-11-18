# NDJSON and JSONL

A file with one JSON value per line - logs, event streams, data exports - opens as a list of its
records. Pick one and it opens in the full [JSON tree](json.md) beside the list, with everything
the JSON view offers: paths, hints, schemas and tables.

Like every view, it's ready at once: you can browse the first records while the rest of the file
is still being indexed, and an export of millions of lines costs well under a megabyte of memory
per gigabyte.

Argonaut recognises NDJSON when the first line ends with `}` and the next begins with `{`. A file
it opens as JSON or text instead can be switched to NDJSON from the view switcher in the status
bar.

To work with only part of a very large file, open a range of its lines from the
[command line](../guide/command-line.md): `Argonaut big.ndjson --lines 1m-2m`.
