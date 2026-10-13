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

## Recommendation

Build A's pieces first (parser and locator). B needs both of them, so nothing is wasted. Then
build B as `--lines` and keep A as `--goto`, or as what the escape chip does. Once
`RangeByteOrigin` exists, an in-app "Open selection as document" (select rows in Raw, isolate
them) costs very little, so the feature is not CLI-only.

Open questions:

- Should a range be 1-based and inclusive (`--lines 1-10` = the first ten lines, like `sed -n`)?
  Proposed: yes.
- In B, is relative numbering acceptable for v1?
- Should the CSV header be fixed in v1, or is CSV out of scope at first?
