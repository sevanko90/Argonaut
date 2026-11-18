# Text

Anything Argonaut doesn't recognise opens here - and it's not the poor relation that sounds like.
It's built the same windowed way as every other view, so a multi-gigabyte log scrolls smoothly from
the moment it opens, and the index behind it costs about 2.5 MB per gigabyte.

## Reading

- **Immediate display at any size**, with smooth scrolling from the first line to the last.
- **Wrapping at a width you choose.** Pathologically long lines - several gigabytes of minified
  JSON on one line - are broken up so there's something useful to read, and a mark in the gutter
  shows where a line was *broken* rather than where it *ended*.
- **The caret's position** in the status bar: byte offset, line, column and how much is selected.
  On a pathological line the column shows a dash rather than stall the window counting
  characters; the line number is always right.
- **The character under the caret is named**, not just shown - `U+2028 LINE SEPARATOR`,
  `U+00A0 NO-BREAK SPACE`, `U+200B ZERO WIDTH SPACE` - which is how you find the invisible thing
  breaking your file.
- **Syntax colouring** for JSON, JSONC and NDJSON, and for config files - YAML, INI,
  `.properties`, `.env`, `.editorconfig` and simple TOML. It's picked from the file name or the
  content, and you can change it or turn it off from the toolbar.
- **[Value hints](../features/hints.md)** on values the colouring picks out, and on unambiguous
  things anywhere - links, ISO dates, JWTs, hex colours, Base64 pictures - in logs, comments and
  plain text.

!!! note "Why a fixed wrap width?"

    Wrapping to the window would mean measuring every line against the font and screen and
    rebuilding the index on every resize. A chosen width keeps every file, however large,
    instant to scroll.

## Editing

Edit in place, with undo and redo, then save or save as. Your edits are held on top of the file
rather than in a copy of it, so editing a multi-gigabyte file is as quick as opening one.

Saving writes a copy beside the file and swaps it in, so a failed save never leaves you with half
a file. Search pauses while there are unsaved edits.

## Line endings

The status bar shows whether the file uses LF, CRLF or a mix. New lines you type follow the
file's own convention - or your operating system's, in a new document - and pasted text is
converted to match. To convert the whole file, choose **Line endings** from the **Transform**
menu; the change is written when you save.

## Transforms

The **Transform** menu encodes and decodes the whole document - JSON string, URL, Base64 - and
runs recipes that turn a list of lines into a SQL `IN (...)` list, a JSON array or a
comma-separated list, and back. See [Text transforms](../features/transforms.md).
