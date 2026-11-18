# Search

Press <kbd>Ctrl</kbd>+<kbd>F</kbd> / <kbd>Cmd</kbd>+<kbd>F</kbd> and type. Argonaut searches the
whole file in the background and highlights matches as it finds them - and as you scroll them into
view - so the window never freezes, however big the file.

- <kbd>Enter</kbd>, <kbd>F3</kbd> or <kbd>Ctrl</kbd>+<kbd>G</kbd> / <kbd>Cmd</kbd>+<kbd>G</kbd>
  goes to the next match; add <kbd>Shift</kbd> for the previous one.
- <kbd>Esc</kbd> clears the search.

## Searching structured files

Search runs against the file's bytes, not what's drawn on screen. In the JSON view that means you
search for text as it's written in the file - so to find a property with a particular value,
include the quotes:

| | |
| --- | --- |
| :x: | `name:value` |
| :white_check_mark: | `"name":value` |

In a [JSON diff](../features/diff.md), search covers both files, and next and previous step
between the rows that match rather than each individual match.

## While editing

Search is paused in the text view while you have unsaved edits, and comes back when you save.
