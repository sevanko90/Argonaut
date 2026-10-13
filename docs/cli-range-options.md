# Opening a line range from the command line

Goal: `argonaut big.ndjson --lines 1000000-2000000` shows lines 1M-2M of a multi-GB file.
Two shapes: **A** opens the whole file and jumps to the range; **B** opens a view over only that
range, with nothing outside it reachable.

## Shared by both

**Argument parsing.** `App.OnFrameworkInitializationCompleted` already filters `-`-prefixed args
out of the positional paths, so a flag breaks nothing today. A small `StartupRequest` parser
(`Shell`) turns `--lines A-B` (also `A-`, `A`, `--bytes A-B`, and `1m`/`2.5m` suffixes)
into a request that `MainWindow.OpenInitialFileAsync` carries. The macOS `Activated` path is
not involved: the range only ever comes from argv.

**Line number to byte offset.** No index we have answers "where does line N start" before a
view is open, and the views disagree on what a line is (raw counts `\n`, CSV counts records,
which can span lines). So add an engine-level `LineRangeLocator` (`Engine/Indexing/Lines`):

- a forward scan from 0 counting `\n` with `span.Count((byte)'\n')` per chunk (vectorised), then
  `IndexOf` inside the chunk where the target falls; stops at line B, never reads past it
- follows the `IByteSource` rules: advances by the length returned, re-reads `AvailableLength`,
  `WaitForLength` on an unsettled source - so a streamed source works the same way
- runs on the background with a progress entry; no allocation beyond the scan itself
- cost: it reads everything before line B once. At ~500 B/line, line 2M is ~1 GB, which is
  around a second cold and well under that from page cache

Result: a `ByteRange` from the start of line A to the end of line B (trailing newline
included), clamped at EOF. Line A beyond EOF is a toast, not an error.

## Option A - open the whole file, jump to the range

The shell already does "open, then reveal a byte range": `SwitchViewAsync(kind, reveal)` loads a
document and calls `IByteRangeNavigable.RevealByteRangeAsync` on it. That is what the JSON
banner's "Line N" link and "Show in text view" use. `OpenOriginAsync` needs the same optional
`reveal` argument.

What you get per view:

| View | Behaviour |
| --- | --- |
| Raw | Jumps to and flashes line A; line numbers are the file's real ones |
| NdJson | Selects line A |
| Json | Selects the node at that offset (for pretty-printed JSON, line A is somewhere in the tree) |
| Csv | Nothing yet: it does not implement `IByteRangeNavigable`. Needs adding, with a record-at-offset lookup |

The views index the whole file in the background as usual, and the reveal waits until the
index covers line A.

The range's end is only used to place you; it is not a boundary. Selecting the whole range is
possible (Raw's reveal takes a range), but a 500 MB selection is not useful, so A should reveal
the start only.

**Effort: small, about 1-2 days.** Parser, locator plus tests (including `GrowingByteSource`),
the `reveal` argument on `OpenOriginAsync`, and the startup wiring. CSV navigation is another day
if wanted.

## Option B - a virtual view of just the range

The building block already exists. `IByteOrigin.OpenRange` is how an NDJSON line, a JSON
array's table, and each search chunk become documents of their own. So B is a decorator:

```csharp
// Engine/Bytes
sealed class RangeByteOrigin(IByteOrigin inner, ByteRange range, string label) : IByteOrigin
{
    public string DisplayName => $"{inner.DisplayName} ({label})";   // "big.ndjson (lines 1,000,000-2,000,000)"
    public string? Path => null;                                    // see below
    public long AvailableLength => range.Length;                    // always settled
    public IByteSource Open() => inner.OpenRange(range.Offset, range.Length);
    public IByteSource OpenRange(long o, long l) => inner.OpenRange(range.Offset + o, l);
    public KeptIndexes KeptIndexes { get; } = new();
    public void Dispose() => inner.Dispose();
}
```

Every view, search, file-type detection and view switching then work unchanged and only ever see
the range: a mapping of only the range, an index of only the range, and a scrollbar the size of
the range. **That is the real win over A: indexing and memory scale with the range, not the
file.** One million lines out of a 20 GB file indexes in about the time of a 500 MB file.

`Path => null` is the right value. It is the documented signal for "not a file", so recent files,
the schema sidecar and the schema binding skip it without new code. The shell's save turns into
Save As (`MainWindowViewModel` line ~1152: `chooseDestination || Path is null`), so editing the
slice and saving writes **a new file** and never overwrites the original. That doubles as
"extract lines to file".

What B has to deal with, roughly in order of cost:

1. **Line numbers and offsets are relative.** Raw's gutter shows 1.. rather than 1,000,000..,
   and the caret readout, NdJson line column and failure banners also count from the range start.
   The fix is a `DisplayBase` (first line number, base offset) on the origin, applied in the
   places that render a number: `RawTextSurface.DrawLineNumber`, the caret readout, the NdJson
   line column, and the JSON error banner. Medium. v1 could ship relative numbering with the
   range in the title.
2. **CSV loses its header.** Lines 1M-2M have no header row, so the first data row becomes the
   column names. Two fixes:
   - a two-piece source (header line + range). `GetContiguousSpan` already allows a split, but
     `RequireContiguous` over a range that straddles the join would throw, so the CSV reader's
     reads need auditing. Harder.
   - preferred: a CSV-specific "header from" option that reads the header from the parent origin
     and passes it in, keeping the source single-piece. Medium.
3. **Pretty-printed JSON slices are not JSON.** Lines 1M-2M of an indented document fail
   detection and open in Raw or the incompatible placeholder. Expected, but worth a sentence in
   the toast. NDJSON, CSV (with 2) and plain text are the real targets.
4. **A way out.** A "Showing lines X-Y · Open full file" chip that reopens the inner origin
   with an Option A reveal at the current position. Small once A exists.
5. **Diff and compare.** Comparing two ranges falls out for free (two `RangeByteOrigin`s) but is
   not needed for v1.

**Effort:** the core (decorator, locator, parser, wiring, relative numbering) is **about 2-3
days**. Absolute numbering, the CSV header and the escape chip make it **about a week**.

## Decision and plan

**Option B is chosen.** Targets are NDJSON, Raw and CSV. Pretty-printed JSON slices are out of
scope (they fail detection and land in Raw, which is fine).

Requirements:

- `--lines A-B` opens lines A..B (1-based, inclusive, like `sed -n`) as an excerpt. `A-` runs to
  the end. `1m`/`2.5m`/`500k` suffixes allowed.
- `--bytes A-B` opens a byte range, for files whose lines are too long to ask for by line (a
  1 GB minified document is one line, so `--lines` can only open all of it). Zero-based and
  inclusive like an HTTP range; `A-`, `A+COUNT`, `k`/`m`/`g` suffixes and hex. No scan: the
  start backs off up to 3 bytes to a UTF-8 character start, the end is cut at EOF. The view is
  detected from the slice (usually Raw); `--view` still overrides. `--lines` that lands on one
  line over 64 MB points at `--bytes`. Lines as wrapped rows were rejected: their meaning would
  change with the wrap width.
- A byte excerpt does not know its first line number, so numbering "as in the file" shows file
  offsets with lines counted from 1. Counting the newlines before it in the background is a
  possible later addition, deliberately not done now.
- `--view json|ndjson|csv|tsv|raw` forces the view instead of detection. Works with or without
  `--lines`.
- Line numbers in an excerpt are switchable between **source** (the file's real numbers, the
  default) and **1-based** (counted from the excerpt's start). One shared setting, honoured by
  every view that shows line numbers or offsets (Raw gutter and caret readout, NdJson line
  column and status, CSV gutter).
- CSV in an excerpt takes its header row from the whole file's line 1 when the excerpt does not
  start at line 1.
- CSV shows line numbers in a pinned gutter like Raw.
- "Open full file" escape: a chip that reopens the whole file at the current position.
- Command-line reference: `--help` (`-h`, and `/?` on Windows) prints the usage text to the
  console and exits without opening a window. The same text appears in a "Command line" section
  of the About box, so it can be found by someone who launched from a shortcut. One source for
  the text.

### Order

1. **This branch (`feature/cli-range`)** - excerpt and command line, on the existing views:
   - [x] `LineRangeLocator` (Engine/Indexing/Lines) + tests, including growing and split sources
   - [x] `ExcerptByteOrigin` (Engine/Bytes) + tests
   - [x] command-line parser (`--lines`, `--bytes`, `--view`, `--help`) + tests; help text shared with About
   - [x] `--help` console output and exit before Avalonia starts (Windows: attach to the parent
         console, since the app is `WinExe`)
   - [x] shell: locate on the background with progress, open the excerpt, honour `--view`
   - [x] shared line numbering (source / 1-based) applied in Raw and NdJson
   - [x] CSV header taken from the whole file
   - [x] `--bytes` excerpts (no scan; UTF-8 start snap; long-line hint on `--lines`)
   - [x] excerpt control: a title-bar pill beside the file name, its range rounded to k/m/g
         (exact in the tooltip and flyout), with a flyout for numbering and "Open all of"
   - [x] About box "Command-line options" link to a window with the `--help` text, which
         `--help` also opens when there is no terminal
   CSV stays on TableView here, with no gutter yet.
2. **Next branch** - a custom `TableSurface` on `Ui/Rows/RowSurface` in `Ui/TableGrid`, CSV
   first, with the pinned line-number gutter. TableView has no frozen columns, so a gutter there
   would scroll away; the surface also stops realising a control per cell. About 3-4 days.
3. **Then** - move the JSON array table onto `TableSurface` and delete `TableGridColumns`, so
   there is one grid, not two. About 2 days.

UI looks (chip, About section, CSV gutter) are shown as screenshots with variants to choose
from before they are settled.
