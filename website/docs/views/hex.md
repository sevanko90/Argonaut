# Hex

Binary files and responses - images, archives, databases, anything with NUL bytes near the start -
open as a hex dump: offsets, the bytes, and their text. Any document can be switched to Hex from
the view switcher, and like every view it opens at once at any size.

## Moving and selecting

- **Click or drag** in either the bytes or the text panel; <kbd>Tab</kbd> switches which one the
  keys act in. Arrow keys, Page Up/Down and Home/End move; <kbd>Shift</kbd> extends the selection.
- **Group bytes** by 1, 2, 4 or 8 from the toolbar. The caret moves a group at a time.
- **Go to offset** - click the offset in the status bar, or <kbd>Ctrl</kbd>+<kbd>L</kbd> /
  <kbd>Cmd</kbd>+<kbd>L</kbd> - and type decimal, hex (`0x1F40`) or a step from the caret (`+16`).
- **Read the text panel as ASCII or UTF-8.** The status bar names the character under the caret.
- Switching to and from the text or JSON view keeps your place.

## Reading values

The **Values** panel reads the selected bytes - 1 to 8 of them - as integers of any width, floating
point, Unix times and characters, in little- or big-endian. With nothing selected it reads the
caret's group. Hover a value to see which bytes it reads; click it to copy it. The **−** and **+**
beside the width narrow or widen what's read.

A selection of 1 to 8 bytes **keeps its width** as you move, so you can read the same kind of value
at place after place:

- Click somewhere else and the same number of bytes is selected there. Hovering shows what a click
  would select.
- Arrow keys move the selection a group at a time; <kbd>Option</kbd> / <kbd>Alt</kbd> + arrow moves
  it by its own width, to walk through a run of values.
- <kbd>Esc</kbd> or a double-click goes back to a single caret.

## Copying

<kbd>Ctrl</kbd>+<kbd>C</kbd> / <kbd>Cmd</kbd>+<kbd>C</kbd> copies the selection as the active panel
shows it - hex text or characters. The right-click menu also copies the raw bytes themselves, which
paste back into Argonaut as the same bytes.

## Saving and opening elsewhere

For a binary response, **Save as…** keeps a copy and **Open in default app** hands it to whatever
your system uses for that kind of file. The toolbar names the format when Argonaut recognises it -
PNG image, ZIP archive, SQLite database and so on.
