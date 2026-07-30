# Argonaut

A cross-platform file viewer built for large files — multi-gigabyte JSON and NDJSON documents open and navigate smoothly, not just small ones.

## Features

- Instantly view any file contents - if the type can't be auto-detected the file is shown in a basic text view.
- Specialised views for different file types
- Files of any size are navigable almost instantly, no "loading..." spinner here! Argonaut never loads the whole file before starting to display it.
- Fast search and highlighting across multi-gb files
- Searches run in the background and results are shown as soon as they become available
- Recent files list
- Light/dark theming, following the OS by default with an in-app override


### Specialised file type support

#### JSON 

Argonaut was originally concieved as a viewer for large JSON files and it has first class JSON support:

- Collapsable nodes
- JSONPath display of selected node and go to JSONPath node option
- Inline decoding of JS dates to readable form
- Copy property value or JSONPath to clipboard

#### NDJson / JSONL

- Browse individual lines, view the JSON for the selected line in the dedicated JSON viewer

#### CSV

- Comma and tab delimited files are shown in a column viewer

## Tech stack

Argonaut is a .NET application built on [Avalonia](https://avaloniaui.net/), a cross-platform XAML-based UI framework. This gives it a single C#/XAML codebase that runs natively on Windows, macOS, and Linux.

... And Claude! Let's not forget the agent that did the work. I'll take the credit for telling it what to do, and how to do it, and knowing what good looks like. But I'm not going to lie, I didn't type a single line of this app myself. We really are living in the future.

### Memory use
Argonaut doesn't "load" a file in the traditional sense - that's how it gets it speed. Instead of loading a multi GB file into RAM then trying to display it all (which takes time), Argonaut maintains a small viewable "window" into the larger file.

To power this view, Argonaut first needs to index the file so it can navigate it (it needs to know where lines start, so it knows where to go when the view wants to display line 5000). The indexing process runs in the background and the file displays as soon as there is enough index to drive the view.

This works well for line based files like raw text and CSV - the index is a relatively small size compared to the file. 

For JSON though, things get a bit more interesting.The index needs to hold a lot more than just line start, it needs to hold the position of every token (array, property, etc) so the index size is related to the depth and complexity of the JSON.

It is possible (even likely) that the index for a complex JSON file could be bigger than the file itself! So Argonaut may take more RAM to load a large file than other, slower viewers. This is the tradeoff for fast viewing of large files. I think it's worth it, but YMMV.


## Running the code

I use [JetBrains Rider](https://www.jetbrains.com/rider/) for compiling / running / tweaking, or you can just download the [.NET runtime](https://dotnet.microsoft.com/en-us/download) and call

    dotnet publish
in the application folder. 

### Running the MacOS release

MacOS quarantines downloaded applictions that have not been signed and refuses to run them.

You have a couple of options. You could (quite rightly!) not trust some random app you download from the internet, and build it from source.

Or, extrscting the zip (to leave you with a .app), moving that .app to another folder (like ~/Applications) then running this in the console seemed to work for me.

    xattr -cr ~/path/to/Argonaut.app

Or, you can wait until I get an Apple developer account to properly sign things. That might take a while!

## Updates

Starting with this release, Windows and macOS builds check GitHub Releases for updates on
launch (at most once every 24 hours) and offer to download and apply them - no manual
re-download needed going forward.

**If you installed Argonaut before this release**, you're on the old plain-zip/portable build
and have no updater wired up. You'll need to manually download this release once (the
`Setup.exe` on Windows, or the `.app`/`.pkg` on macOS); every release after that will offer to
update itself automatically.

Linux still ships as a plain zip with no auto-update, for now.
