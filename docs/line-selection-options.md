# Opening a selection of lines from a filter file

Goal: a third-party app writes a filter file naming many lines (or byte ranges) of one big file,
and `argonaut big.ndjson --lines --filter picks.txt` opens just those lines as one read-only view,
following the filter file as it changes.

This builds on the excerpt work (`docs/cli-range-options.md`): an excerpt is one window into the
file; a selection is many windows laid end to end.

## Decided

- **Syntax.** `--lines --filter <path>` reads line entries; `--bytes --filter <path>` reads byte
  ranges. Errors: `--lines`/`--bytes` with neither a range nor `--filter`; a range and `--filter`
  together; `--filter` alone; `--filter` with a second file. `--view` may force Raw, CSV/TSV or
  NDJSON; JSON is refused (a selection of lines is not one JSON document).
- **Entries.** A lines filter holds `N` and `A-B` (`A-` to the end, `k`/`m` suffixes, as
  `--lines` reads them). A bytes filter holds byte ranges as `--bytes` reads them (0-based,
  inclusive, `k`/`m`/`g`, hex), plus a single byte. Each is taken as given, its start snapped back
  to a UTF-8 character start as `--bytes` does - not rounded out to whole lines, which on a file
  of huge lines would turn a small range into the whole file. Entries are separated by commas,
  newlines or both. Lines and bytes are never mixed in one file.
- **Order.** File order, with overlaps and duplicates merged. Scrolling then reads the file
  sequentially, neighbouring entries share a run, and file line to view line stays monotonic,
  which is what a live reload needs to keep its place.
- **Window, not copy.** The view reads the selected lines where they are in the file. Copying a
  10 GB selection to a temp file was rejected for the time it takes, not the disk.
- **Comments and blank lines.** Blank lines are skipped and anything after `#` on a line is
  ignored.
- **Past the end of the file.** Entries are clipped to the file, and a toast says how many were
  dropped - a live filter may be written for a file that is still growing.
- **Empty selection.** At startup the view is not opened and a toast says why. On a live reload
  the view stays open and empty, saying "0 lines selected", until more entries are written.
- **Strictly read-only.** No edit, no Save, no Save As.
- **Live.** The view follows changes to the filter file.

## Design

**Parsing (`Engine`).** A `SelectionFilter` parser reads the filter file as a stream into a list
of ranges, sorts them and merges overlaps and neighbours. An entry it cannot read fails the whole
file, naming the filter file line it is on. Cost: 16 bytes per entry before merging.

**Locating (`Engine/Indexing/Lines`).** A `LineSelectionLocator` turns the merged list into runs
in one forward pass, the same vectorised newline count `LineRangeLocator` uses, stopping at the
last selected line. Each run is whole lines: view start, file start, length, first file line.
Byte entries need no scan at all. The pass also records the newline count at each 4 MB chunk
it reads (`LineCheckpoints`), so a reload jumps to the nearest instead of rescanning the file.
Runs off the UI thread with progress and a stop, like `--lines` today.

**The origin (`Engine/Bytes`).** A `SelectionByteOrigin` holds the run table and the whole file.

- `Open()` maps the whole file once and returns a source that windows it through the run table.
  `GetContiguousSpan` finds the run by binary search and returns a zero-copy span cut at the run's
  end, which the `IByteSource` contract allows. `CopyTo` gathers across runs.
- `OpenRange()` (search chunks, the NDJSON line's own document) maps only the file span its runs
  cover and windows that, so a search chunk owns its mapping as it must.
- A run that does not end in a newline - the file's last line, or a byte entry ending mid-line -
  gets a 1-byte `\n` from memory, so every run ends a line and lines never join across a seam.
- `Path` is null, so recents and sidecars skip it. A new `IsReadOnly` on `IByteOrigin` (false by
  default) turns off editing, Save and Save As.

**What crosses a seam.** Because every run ends a line, anything that reads one line or less stays
inside one run: `LineReader`, `DisplayText`, CSV fields, the NDJSON line's tree, the detector's
first-line checks. The readers of longer ranges:

| Reader | Change |
| --- | --- |
| `SearchSession` chunk | Take the length the source returns; at a seam, start the next chunk there with no overlap, so a match never spans two runs that are not neighbours in the file |
| Raw rows | None: `RawRowReader` already gathers |
| Index scans (`FileOffsetIndex`, raw segments, detector finders) | None: they already advance by the length returned |

**Numbering.** The origin carries its `LineNumbering` built with `FromRuns`. "As in the file"
shows file line numbers (byte filters: each line's file offset, as a byte excerpt does now); "from
1" counts the view's lines. The Raw gutter and caret, the NDJSON gutter and status, and the CSV
gutter in step 2 all read it. Memory: 32 bytes per run, so a filter of 1M lines that are not
neighbours costs 32 MB.

**Shell.** Opening runs the locator with progress, then opens the selection like an excerpt. The
title-bar pill reads "1,204 lines selected", and its flyout holds the numbering choice, the
filter file's name, and "Open all of", which opens the whole file at the line the view is on.

**Live updates.** The filter-file service (`ISelectionFilterFiles`, composed in the main window
and injected - no static file access) watches the filter file's folder for it, so a writer that
replaces the file is seen as well as one that writes in place. Writes are settled for 300 ms
before the file is read, so a writer part way through a burst is not read half-written. The
locator keeps `LineCheckpoints` - the newline count at each 4 MB chunk it read, about a thousand
for a 4 GB file - and a reload starts each range from the nearest one, so it reads a chunk or two
per place the filter points at rather than the file. The selection reopens in the same view, on
the line that was selected (or the next one still in it). A filter that no longer reads keeps
the last good view and says why in a toast. Opening anything else stops the watch.

**Later: one pass.** The locator already knows each selected line's start and length, so it could
hand the view a ready-made line index instead of the view scanning the selected bytes again. That
needs index constructors that take already-scanned data, so it waits until the rest works.

## Steps

1. [x] `--filter` parsing in `StartupArguments`, and the `SelectionFilter` parser with tests
2. [x] `LineSelectionLocator` (lines and bytes), tested against a naive split and over
       `GrowingByteSource`; resumes from `LineCheckpoints` on a reload
3. [x] `SelectionByteOrigin` and its sources, tested against a byte-by-byte copy of the selection
4. [x] `SearchSession` at seams; `IsReadOnly` through Raw editing and Save/Save As
5. [x] Numbering from the origin's runs (lines and offsets)
6. [x] Shell: open flow, pill and flyout, `--help` text
7. [x] Live updates: the watcher service, reload keeping the file line, toast on a bad filter
8. [ ] Later: the one-pass line index

About 4-5 days to step 7. The CSV gutter (step 2 of the excerpt plan) follows this.
