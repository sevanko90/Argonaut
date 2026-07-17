# Argonaut

A cross-platform JSON viewer built for large files — multi-gigabyte JSON and NDJSON documents open and navigate smoothly, not just small ones.

## Fast, low memory footprint

Argonaut never loads a document into memory as a DOM or parses it into objects up front. Files are memory-mapped and structurally indexed with a single streaming pass, storing only compact per-token metadata (kind, offset, length, parent/sibling links) rather than decoded text. Node text, values, and JSONPath segments are decoded lazily, on demand, straight from the memory-mapped file — only for whatever is currently visible or selected. The result is a viewer that stays responsive and keeps a small, largely constant memory footprint regardless of whether the file is 10 MB or 10 GB.

## Features

- Collapsible tree view over JSON documents, with lazy paging for very large arrays/objects
- NDJSON support: browse individual lines and view each one's JSON structure independently
- Click-to-select any node, with its JSONPath shown live and copyable
- Clickable JSONPath breadcrumbs to navigate straight back up the tree
- Recent files list
- Light/dark theming, following the OS by default with an in-app override

## Tech stack

Argonaut is a .NET application built on [Avalonia](https://avaloniaui.net/), a cross-platform XAML-based UI framework. This gives it a single C#/XAML codebase that runs natively on Windows, macOS, and Linux.

## Limitations

- A single top-level JSON document is limited to about 4 GB, due to the internal structural index using 32-bit file offsets.
- NDJSON files have no such ceiling on overall file size — each line is indexed and mapped independently, so the practical limit is disk space rather than a hard byte cap. The 4 GB limit above still applies to any *individual* line's JSON.
