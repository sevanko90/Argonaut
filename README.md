# Argonaut

A cross-platform file viewer built for large files — multi-gigabyte files (including JSON and NDJSON) open and navigate smoothly.

## Features

- Instantly view any file contents - anything Argonaut can't identify opens in the text view, and you can try any view on any file from the status bar if the auto detection fails. 
- Paste data straight in - the clipboard opens as a document, no file needed
- Specialised views for different file types
- Value hints - dates, colours, tokens, links and encoded payloads are explained right beside the value, and the ones hiding a document open up to show it
- Edit and save in the text view, even multi-gigabyte files
- Files of any size are navigable almost instantly, no "loading..." spinner here! Argonaut never loads the whole file before starting to display it.
- Fast search and highlighting across multi-gb files
- Searches run in the background and results are shown as soon as they become available
- Recent files list
- Light/dark theming, following the OS by default with an in-app override

### Pasting data

Copied a chunk of JSON out of a log, an API response or a query result? Paste it straight in - hit
the **Paste data** button on the start screen, or Ctrl/Cmd+V before you've opened anything. It gets
the same treatment a file does: detected by its *content* rather than a file name it doesn't have,
so pasted JSON, NDJSON and CSV all land in the right view, with search, the JSON tree and
everything else working as normal.

A few things differ, because a pasted document isn't a file:

- It won't appear in your recent files - there'd be nothing to reopen.
- The per-file schema conveniences don't apply (no `<file>.schema.json` sidecar, and your schema
  choice isn't remembered for next time). You can still pick a schema from the toolbar by hand.
- There's a 64 MB ceiling. Clipboards will happily hold more, but at that point you're better off
  saving it to a file and opening that - which is what Argonaut is built for anyway.

### Specialised file type support

#### JSON 

Argonaut was originally conceived as a viewer for large JSON files (hence the name!) and it has first-class JSON support:

- Displays documentation from JSON Schema files
- Semantic JSON Diff
- Collapsible nodes, with optional indent guides
- Open JSON Arrays in a grid view for slicing them by different dimensions
- JSONPath display of selected node and go to JSONPath node option
- Value hints beside the values that need them - see [Value hints](#value-hints) below
- Copy property value or JSONPath to clipboard

#### NDJson / JSONL

- Browse individual lines, view the JSON for the selected line in the dedicated JSON viewer

#### CSV

- Comma and tab delimited files are shown in a column viewer

#### Text view

The fallback view - but not the poor relation that sounds like. It's built the same windowed way as
everything else, so a multi-gigabyte log scrolls smoothly from the moment it opens. The index behind it is deliberately sparse (one anchor per 64 display rows), so it costs very little memory no matter how big the file is.

- Smooth scrolling and immediate display at any file size
- Force-wraps pathologically long lines (eg several GB of minified JSON) for a useful display
- Choose your wrap width, with a marker in the gutter showing where a long line was *broken* rather
  than where it actually *ended*
  - Explicit wrap widths rather than word-wrap to the window is a deliberate performance-based choice. Window-width soft wrapping of unicode documents requires accounting for monitor DPI and font metrics, then re-building the underlying index that drives the document view on every window resize. That cripples display performance, and Argonaut is all about speed!
- The status gutter tracks the caret: byte offset, line, column, and how much is selected. On a
  pathological line the column gives up and
  shows a dash rather than stalling the UI to count characters; the line number is always right.
- **The character under the caret is named, not just shown** - `U+2028 LINE SEPARATOR`,
  `U+00A0 NO-BREAK SPACE`, `U+200B ZERO WIDTH SPACE`. This is how you find the invisible thing
  that's breaking your file. Names come from the Unicode Character Database 16.0.0, since .NET
  exposes character categories but no names.
- Double-click to select a word
- Edit in place, with undo and redo, then save or save as. Your edits are held on top of the file
  rather than a copy of it, so editing a multi-gigabyte file is as quick as opening one. Saving
  writes a copy beside the file and swaps it in, so a failed save never leaves you with half a
  file. (Search is paused while there are unsaved edits.)
- Value hints, as in the JSON view: on values the syntax colouring picks out, and on unambiguous
  things anywhere else - links, ISO dates, JWTs, hex colours, Base64 pictures - in logs, comments
  and plain text
- Syntax colouring for config-style files, in both viewing and editing:
  - JSON, JSONC and NDJSON - keys, strings, numbers, `true`/`false`/`null`, comments
  - Config files - YAML, INI, `.properties`, `.env`, `.editorconfig`, simple TOML: keys,
    values, sections, comments and YAML's list markers
  - Picked automatically from the file name, or from the content for a paste or an unfamiliar
    extension; override or turn it off from the toolbar
  - Colouring works one line at a time, so things that span lines (a multi-line YAML block, an
    unclosed `/* */`) aren't coloured across them
  - On very long lines (eg minified JSON), colour holds near the start of the line and while
    you scroll smoothly down from there; jump far into the line and the rest of it shows
    uncoloured rather than risk wrong colours

### JSON Schema support for documentation

Bind a JSON Schema to an open document and Argonaut shows what the data *means* — `title` and
`description` from the schema, in a resizable gutter down the left, with the full text on hover.
This is documentation only - we're not validating correctness. Argonaut attempts to match any selected schema to the document by testing properties, nothing bad happens if it can't find a match. 

- Pick a schema from the toolbar - bundled ones, or your own dropped in the schemas folder. A
  `<file>.schema.json` sidecar is picked up automatically, and your choice is remembered per file.
- Documents object properties, positional array slots (`prefixItems`) and enum value labels.
- Files holding many schemas (`$defs`, or an OpenAPI document's components) offer a type picker,
  ranked by how well each type matches the document.
- Handles local `$ref` (including recursive), `$defs`/`definitions`, and `allOf`/`oneOf`/`anyOf`.
- Ignored: remote `$ref`, `patternProperties`, `if`/`then`/`else`.

### Semantic JSON Diff

Unlike regular diff tools, Argonaut understands that JSON structure carries a *meaning* and uses this to show real differences in the data instead of just reporting that a line changed. This means that, for example, moving a property from the top of an object to the bottom won't show as a diff because in JSON terms the object it represents is unchanged.

This also means that you can't rely on the visible node structure of the target document being the same as the underlying file (because the two views are synchronised to the shape of the source document). If you want to find something in the target document, use the JSONPath view to show exactly where it is. 

### Value hints

Plenty of values in data files are unreadable as written. Argonaut reads them for you and says what
they are, right next to the value:

- **Dates** - epoch numbers (milliseconds, seconds, or Keepa minutes - Argonaut works out which from
  the document and you can change it) and ISO 8601 strings, with how long ago or how far off they are
- **Colours** - a swatch, and the colour in the other notation (`#1E90FF` reads as `rgb(30, 144, 255)`)
- **JWTs** - the algorithm and when the token expires, or how long ago it did
- **Links** - web and mail addresses open with a click (Cmd/Ctrl+click on the value itself)
- **Cron schedules** - in words, with when it next runs
- **JSON inside a string** - and **Base64**, named for what it decodes to: text, PNG/GIF/JPEG/WebP
  images (with their size), PDF, ZIP or gzip

A hint ending in a chevron opens up: a JWT shows its header and claims, embedded JSON shows as a tree,
a Base64 picture shows the picture - and the rest can be opened as a document of their own. A
date's hint lets you change how the file's numbers are read.

Hints go by what a value looks like, never by the name of its property, so they work on any file.
In the text view they're stricter outside the values syntax colouring finds - a number in a log is
just a number - and show as an underline that reveals the hint when you hover it.

### Searching
Argonaut searches files in the background and highlights matches as it finds them (or as you scroll them into view) - searches never block the UI. 

#### JSON searching
Searches run against the underlying file, not the rendered display. This means that if you want to search for a specific `property:value` instance you need to search for it as it would appear in the file, quoting the property:


❌: `property:value`  
✅: `"property":value`

For the JSON Diff view, searches run across both files but the next/previous options move to the next or previous relevant row, not the specific search match. This is by design and keeps the next/previous behaviour intuitive. 

### Command line

    Argonaut [FILE [FILE2]] [--lines RANGE | --bytes RANGE | --lines --filter FILE | --bytes --filter FILE] [--view VIEW]

Give two files to compare them, when both are JSON.

#### Opening part of a file
`--lines` and `--bytes` open just part of a file as a document of its own. Memory and indexing time follow the size of the part, not the file, so you can look at a slice of a multi-GB file straight away.

- `--lines RANGE` opens those lines. Lines are numbered from 1. RANGE is `A-B`, `A-` (to the end), `A+COUNT`, or a single line `A`, and numbers can use `k` or `m`: `1m-2m`, `2.5m+500k`.
- `--bytes RANGE` opens those bytes. Use it for files whose lines are too long to pick by line, such as minified JSON. Bytes count from 0 and the last byte is included, so `0-499` is the first 500. RANGE is `A-B`, `A-` or `A+COUNT`, and numbers can use `k`, `m` or `g` (thousands, millions, billions) or be hex: `0x1F400`. If the start lands inside a UTF-8 character, it moves back to where that character starts.

A pill next to the file name in the title bar shows the range. From it you can switch between the file's own line numbers and numbering from 1, or open the whole file.

#### Filtering lines or bytes
Use `--filter FILE` with `--lines` or `--bytes`, in place of a range. Every line or byte range listed in FILE opens as one read-only view, in file order. Entries are ranges written as above, separated by commas or newlines, and `#` starts a comment. The view updates when FILE changes, so you can edit the list and watch it follow.

    # picks.txt
    1-100
    5000+20, 1m+10

#### Choosing the view
`--view VIEW` opens the file in `json`, `ndjson`, `csv`, `tsv` or `raw` instead of the detected view.

#### Examples

    Argonaut big.ndjson --lines 1m-2m
    Argonaut export.csv --lines 500k+1000 --view raw
    Argonaut minified.json --bytes 1.5g+100m
    Argonaut big.ndjson --lines --filter picks.txt
    Argonaut before.json after.json

`--help` (or `-h`, and `/?` on Windows) prints this reference. The About box has it too, under "Command-line options".

## Tech stack

Argonaut is a .NET application built on [Avalonia](https://avaloniaui.net/), a cross-platform XAML-based UI framework. This gives it a single C#/XAML codebase that runs natively on Windows, macOS, and Linux.

... And Claude! Let's not forget the agent that did the work. I'll take the credit for telling it what to do, and how to do it, and knowing what good looks like. But I'm not going to lie, I didn't type a single line of this app myself. We really are living in the future.

### Memory use
Argonaut doesn't load a file into RAM. It memory-maps it and shows a small window onto it, backed by an index built in the background - the file is on screen as soon as there's enough index to drive the view.

The indexes are small next to the file. Roughly, per GB of file:

- **CSV and NDJSON:** about 0.3 MB
- **JSON:** under 1 MB
- **Text:** about 2.5 MB

They're small enough to keep, so switching views on the same file (say JSON to text and back) reuses the index instead of rebuilding it - unless the file has changed.

Your OS may show a higher figure for Argonaut: pages of the file it has read count towards its memory, but that's the OS's file cache, and it's given back when something else needs it.

Pasted data is the exception: with no file to map, the pasted bytes stay in memory while the document is open. That's why pasting has a size limit - save a big payload to a file first.

## Running the code

I use [JetBrains Rider](https://www.jetbrains.com/rider/) for compiling / running / tweaking, or you can just download the [.NET runtime](https://dotnet.microsoft.com/en-us/download) and call

    dotnet publish
in the application folder. 

### Or: no local .NET at all

There's a Docker Compose + VS Code Dev Container setup in the repo, so you can build, test and
package Argonaut without installing a .NET SDK on your machine. The commands are the same on
Windows, macOS and Linux — you drive them from your normal shell, no container shell needed:

    docker compose run --rm app dotnet build Argonaut.slnx
    docker compose run --rm app dotnet test Argonaut.Tests/Argonaut.Tests.csproj

To get something you can actually double-click, build in the container and run the binary
natively — no X11 forwarding, no GUI plumbing:

    docker compose run --rm app dotnet publish Argonaut/Argonaut.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

Swap the RID for your machine: `win-x64`, `osx-arm64`, `osx-x64` or `linux-x64`. The result lands
in the usual place, visible from the host:

    Argonaut/bin/Release/net10.0/<rid>/publish/

A Linux container cross-compiles a complete Windows binary — icon and manifest included.

Or open the folder in VS Code and pick **Reopen in Container**. Full details, including
troubleshooting and how to run the UI inside the container if you really want to, are in
[.devcontainer/README.md](.devcontainer/README.md).

## Updates

Windows and macOS builds check GitHub Releases for updates on launch (at most once every 24 hours) and offer to download and apply them - no manual re-download needed going forward.

Linux still ships as a plain zip with no auto-update, for now.

## License

Argonaut is released under the [MIT License](LICENSE). It is built on open-source components whose licences and notices are collected in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which ships with the app (About > Licenses and third-party notices). After adding a package or bumping a version, regenerate it with `python3 scripts/make-third-party-notices.py`.
