# Opening data

Argonaut opens a file the moment you choose it. It decides how to show it from its **contents**,
not its name, and starts drawing as soon as it has read enough for the first screen - the rest is
indexed in the background while you work.

## What it opens it as

| The data | Opens in |
| --- | --- |
| JSON - starts with `{` or `[` | [JSON view](../views/json.md) |
| One JSON object per line | [NDJSON view](../views/ndjson.md) |
| XML | [XML view](../views/xml.md) |
| Comma- or tab-separated rows | [CSV view](../views/csv.md) |
| Binary - a known file signature, or NUL bytes near the start | [Hex view](../views/hex.md) |
| Anything else | [Text view](../views/text.md) |

Detection only reads the start of a file, so a huge file is classified as quickly as a small one.
If it guesses wrong, or you'd like a different angle on the same bytes, pick another view from the
**view switcher** in the status bar. Every file can be shown as text or hex; the structured views
check the file plausibly fits before switching. Switching views on the same file reuses what has
already been indexed, and keeps your place where it can.

## Ways in

From the start screen, or the toolbar once a document is open:

Open file…
:   Any file on disk, of any size. You can also drag a file onto the window. The folder button
    beside it opens the picker in a common configuration folder.

Paste data
:   The clipboard becomes a document - also <kbd>Ctrl</kbd>+<kbd>V</kbd> /
    <kbd>Cmd</kbd>+<kbd>V</kbd> on the start screen. It's detected by content just like a file, so
    pasted JSON, NDJSON and CSV land in the right view. There's a 64 MB limit, because a paste has
    no file to map and is held in memory: save anything larger to a file and open that.

New document
:   An empty text document to type or paste into, <kbd>Ctrl</kbd>+<kbd>N</kbd> /
    <kbd>Cmd</kbd>+<kbd>N</kbd>.

New request
:   The response to an HTTP request, <kbd>Ctrl</kbd>+<kbd>L</kbd> / <kbd>Cmd</kbd>+<kbd>L</kbd>.
    See [below](#http-requests).

The [command line](command-line.md) opens a file, part of a file, or two files to compare.

## HTTP requests

**New request** opens a request pane at the top of the window:

- Choose **GET** or **POST** and enter the URL.
- The URL's query arguments appear as a list underneath. Edit either one and the other follows.
- A POST has a body, edited in a full text editor with colouring and undo.

**Send** downloads the response, with progress and a Stop button, and opens it below the pane
as an ordinary document - in the JSON tree, a table, hex, whatever its content calls for. Search,
schemas and Save As all work on it. Send again to refresh it. If the request fails, you see what
the server said.

A request is saved to your list the first time it succeeds. Change a saved request and Argonaut
asks, when you're done, whether to update it or save it as a new one. The last response to each
saved request is kept, so reopening it shows that response at once - with the time it was fetched -
until you send again. Kept responses are capped at 1 GB in total, oldest first, and **Clear kept
responses** on the start screen deletes them.

## Recent and pinned

The start screen lists what you've opened recently - files and requests - and what you've pinned.

- Star an entry to pin it. Pinned entries stay; the recent list keeps the last five others.
- Rename an entry from its menu. Files also offer **Show in Finder** / **Show in Explorer**, and
  requests **Save response as…**.
- On macOS, a file you've moved or renamed is still found.
- The list is encrypted in Argonaut's settings file, so the paths and URLs in it - and any API
  keys in those URLs - aren't readable from the file itself.

A paste or a new document isn't added, since there's nothing to reopen.
