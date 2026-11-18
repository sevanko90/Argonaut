# Argonaut

**Any file. Any size. On screen at once.**

Argonaut is a cross-platform desktop viewer for files that other tools choke on. Give it a 4 GB
log, a 20 GB NDJSON export, several gigabytes of minified JSON or a binary dump, and it shows the
first screen straight away - it never loads the whole file, so multi-gigabyte files cost a few
megabytes of memory and scroll smoothly from end to end.

It opens anything: files are recognised by their contents, unknown ones open as text and binary
data as hex. On top of that it has specialised views for the formats data people work in:

- **JSON** - a collapsible tree with JSONPaths, arrays as tables, JSON Schema documentation and a
  semantic diff
- **NDJSON / JSONL** - millions of records, each opening in the JSON tree
- **XML** - a searchable tree with XPaths
- **CSV / TSV** - a column grid
- **Text** - syntax colouring, named Unicode characters, editing and saving at any size, text
  transforms
- **Hex** - offsets, bytes and text, with a panel reading the selected bytes as numbers and dates

Plus value hints that decode dates, colours, JWTs and Base64 beside the value, background search,
HTTP requests opened as documents, and a command line for opening part of a huge file.

**[Download and documentation: sevanko90.github.io/Argonaut](https://sevanko90.github.io/Argonaut/)**

Windows and macOS builds update themselves; every version is on the
[releases page](https://github.com/sevanko90/Argonaut/releases).

## Tech stack

Argonaut is a .NET application built on [Avalonia](https://avaloniaui.net/), so one C#/XAML
codebase runs natively on Windows, macOS and Linux. Files are memory-mapped and read through
small background-built indexes - under 1 MB per GB of JSON - and the views draw their own rows,
which is what keeps them smooth at billions of rows. Updates are delivered by
[Velopack](https://velopack.io/).

... And Claude! Let's not forget the agent that did the work. I'll take the credit for telling it
what to do, and how to do it, and knowing what good looks like. But I'm not going to lie, I didn't
type a single line of this app myself. We really are living in the future.

## Building

With the [.NET SDK](https://dotnet.microsoft.com/download) installed:

    dotnet publish Argonaut

The website and user docs live in [`website/`](website) and are built with
[MkDocs Material](https://squidfunk.github.io/mkdocs-material/) by the `Website` workflow. To
preview them locally:

    pip install -r website/requirements.txt
    cd website && mkdocs serve

## License

Argonaut is released under the [MIT License](LICENSE). It is built on open-source components whose
licences and notices are collected in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which
ships with the app (About > Licenses and third-party notices). After adding a package or bumping a
version, regenerate it with `python3 scripts/make-third-party-notices.py`.
