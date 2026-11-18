# Under the hood

## Never load the whole file

Argonaut doesn't read a file into memory. It **memory-maps** it and shows a small window onto it,
backed by an index built in the background - so the file is on screen as soon as there's enough
index to draw the first page, and scrolling anywhere reads only the bytes it needs.

The indexes are small next to the file. Roughly, per gigabyte of file:

| View | Index |
| --- | --- |
| CSV and NDJSON | about 0.3 MB |
| JSON | under 1 MB |
| Text | about 2.5 MB |

They're small enough to keep, so switching views on the same file - JSON to text and back -
reuses the index instead of building it again, unless the file has changed.

Your operating system may show a higher figure for Argonaut: pages of the file it has read count
towards its memory. That's the system's file cache, and it's handed back when something else needs
it.

A paste is the exception: with no file to map, its bytes stay in memory while it's open. That's
why pasting has a 64 MB limit.

## Tech stack

Argonaut is a .NET application built on [Avalonia](https://avaloniaui.net/), a cross-platform UI
framework, so one C# codebase runs natively on Windows, macOS and Linux. The views draw their own
rows rather than using stock list controls, which is what lets them scroll through billions of
rows smoothly. Updates are delivered by [Velopack](https://velopack.io/).

And Claude. The author says what to build, how, and what good looks like; every line of the code
was written by an AI agent.

## Open source

Argonaut is released under the
[MIT License](https://github.com/sevanko90/Argonaut/blob/main/LICENSE). The code, issues and
releases are on [GitHub](https://github.com/sevanko90/Argonaut).
