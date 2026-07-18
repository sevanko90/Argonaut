# Argonaut

A cross-platform JSON viewer built for large files — multi-gigabyte JSON and NDJSON documents open and navigate smoothly, not just small ones.

## Features

- Fast load and view
  - Files of any size are navigable almost instantly, no "loading..." spinner here! Argonaut never loads the whole file, only a small amount of metadata is held in RAM at any point in time.
- Fast search and highlighting across multi-gb files.
  - Searches run in the background and results are shown as soon as they become available
- JSON Support with dedicated JSON viewer
  - Collapsable nodes
  - JSONPath display of selected node and breadcrumb navigation
  - Inline decoding of JS dates to readable form
  - Copy property value to clipboard
- NDJSON support
  - Browse individual lines, view the JSON for the selected line in the dedicated JSON viewer
  - Tested with 3GB+ NSJson files, the practical limit for NDJson is disk space.
- Recent files list
- Light/dark theming, following the OS by default with an in-app override

## Limitations

- Any individual JSON document is limited to about 4 GB, due to the internal structural index using 32-bit file offsets. In practice this shouldn't be a real issue - a single 4GB json file is impractical to consume with most standard DOM parsers anyway but it can be revisited if it becomes necessary.
  - for NDJson files this means any single line is limited to 4GB. The total file can be TBs. 

## Tech stack

Argonaut is a .NET application built on [Avalonia](https://avaloniaui.net/), a cross-platform XAML-based UI framework. This gives it a single C#/XAML codebase that runs natively on Windows, macOS, and Linux.

... And Claude! Let's not forget the agent that did the work. I'll take the credit for telling it what to do, and how to do it, and knowing what good looks like. But I'm not going to lie, I didn't type a single line of this app myself. We really are living in the future.