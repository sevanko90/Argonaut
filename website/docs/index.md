---
title: Any file, any size, on screen at once
hide:
  - navigation
  - toc
---

<div class="argo-hero" markdown>

![Argonaut](assets/logo.svg){ .argo-logo }

# Any file. Any size.<br><span class="argo-gold">On screen at once.</span>

<p class="argo-tagline">
Argonaut is a desktop viewer that opens whatever you give it - a 4 GB log, a 20 GB NDJSON export,
a minified JSON blob, a binary dump - and shows it straight away. No loading bar, no waiting for
the whole file, no running out of memory. Then, for the formats you work with most, it goes
further.
</p>

[Download Argonaut :material-download:](download.md){ .md-button .md-button--primary }
[Read the guide](guide/opening.md){ .md-button }

<span class="argo-platforms">Free and open source · Windows · macOS · Linux</span>

![Total downloads](https://img.shields.io/endpoint?url=https%3A%2F%2Fargonautviewer.com%2Fdownloads.json){ .argo-downloads }

</div>

<div class="grid cards" markdown>

-   :material-file-question-outline:{ .lg .middle } __Opens any file__

    ---

    Argonaut works out what a file is from its contents, not its name. Anything it doesn't
    recognise opens as text, and binary data opens as a hex dump - you never get "unsupported
    file type". Try any view on any file from the status bar.

-   :material-database-outline:{ .lg .middle } __At any size__

    ---

    Files are mapped, not loaded. Argonaut reads only the part you're looking at, so a
    multi-gigabyte file costs a few megabytes of memory, and you can scroll from the first line
    to the last without a pause.

-   :material-lightning-bolt-outline:{ .lg .middle } __Showing it fast__

    ---

    The first screen appears as soon as there's enough to draw it. Indexing carries on in the
    background while you read, search and scroll - nothing waits for the whole file.

</div>

## Specialised where it counts { .argo-section-title }

<p class="argo-lead">
On top of opening everything, Argonaut understands the formats data people live in.
</p>

<div class="grid cards" markdown>

-   :material-code-json:{ .lg .middle } __JSON__

    ---

    A collapsible tree with JSONPaths, arrays opened as tables, documentation from JSON Schema
    and a diff that compares meaning, not lines.

    [:octicons-arrow-right-24: JSON view](views/json.md)

-   :material-format-list-bulleted:{ .lg .middle } __NDJSON and JSONL__

    ---

    Millions of records, one per line - browse them and open any one in the full JSON tree.

    [:octicons-arrow-right-24: NDJSON view](views/ndjson.md)

-   :material-xml:{ .lg .middle } __XML__

    ---

    A searchable tree of any size, with the XPath of where you are, written with the
    document's own prefixes.

    [:octicons-arrow-right-24: XML view](views/xml.md)

-   :material-table:{ .lg .middle } __CSV and TSV__

    ---

    Columns you can read, resize and pan across, however many rows there are.

    [:octicons-arrow-right-24: CSV view](views/csv.md)

-   :material-text-long:{ .lg .middle } __Text__

    ---

    Logs and config files with syntax colouring, a named character under the caret, and
    editing that's as quick on a 4 GB file as a small one.

    [:octicons-arrow-right-24: Text view](views/text.md)

-   :material-hexadecimal:{ .lg .middle } __Hex__

    ---

    Binary files as offsets, bytes and text, with a Values panel that reads the selected bytes
    as integers, floats and dates.

    [:octicons-arrow-right-24: Hex view](views/hex.md)

</div>

## And the things that save you time { .argo-section-title }

<div class="grid cards" markdown>

-   :material-lightbulb-on-outline:{ .lg .middle } __Value hints__

    ---

    Epoch times, colours, JWTs, cron schedules and Base64 payloads are explained right beside
    the value - and the ones hiding a document open to show it.

    [:octicons-arrow-right-24: Value hints](features/hints.md)

-   :material-magnify:{ .lg .middle } __Search that doesn't block__

    ---

    Searches run in the background across the whole file and highlight matches as they're
    found.

    [:octicons-arrow-right-24: Search](guide/search.md)

-   :material-web:{ .lg .middle } __Open an API response__

    ---

    Send a GET or POST and the response opens as a document in whichever view suits it - then
    save the request to run again.

    [:octicons-arrow-right-24: Opening data](guide/opening.md#http-requests)

-   :material-console:{ .lg .middle } __Open part of a file__

    ---

    `--lines 1m-2m` or `--bytes 1.5g+100m` opens just that slice of a huge file, with memory to
    match.

    [:octicons-arrow-right-24: Command line](guide/command-line.md)

-   :material-pencil-outline:{ .lg .middle } __Edit at any size__

    ---

    Edits sit on top of the file rather than a copy of it, so changing a line in a 4 GB file is
    as quick as opening it - and a failed save never leaves half a file.

    [:octicons-arrow-right-24: Text view](views/text.md#editing)

-   :material-clipboard-text-outline:{ .lg .middle } __Paste straight in__

    ---

    Copied JSON out of a log or a query result? Paste it - it's detected and opened in the right
    view, no file needed.

    [:octicons-arrow-right-24: Opening data](guide/opening.md#ways-in)

</div>

## Built for big files { .argo-section-title }

<p class="argo-lead">
Argonaut's indexes are small next to the file they describe. Memory per gigabyte of file:
</p>

<div class="argo-stats" markdown>
<div class="argo-stat"><strong>~0.3 MB</strong><span>CSV and NDJSON</span></div>
<div class="argo-stat"><strong>&lt; 1 MB</strong><span>JSON</span></div>
<div class="argo-stat"><strong>~2.5 MB</strong><span>Text</span></div>
<div class="argo-stat"><strong>0</strong><span>bytes loaded before the first screen</span></div>
</div>

<p class="argo-lead">
<a href="under-the-hood/">How it works</a>
</p>
