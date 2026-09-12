# Showing markdown — options considered

Decision record for "open a `.md` file and see it as markdown rather than as text". Nothing here
is built. It exists to settle *what* "rendering markdown" means in an app whose defining constraint
is that it never holds a document in memory, before any of it is scheduled.

Every cost claim was checked against the code at the time of writing (branch
`raw-pan-range-and-unicode-separators`, 2026-09-12), with file references kept so a later reader can
tell whether the reasoning still holds.

Two features hide behind one phrase, and they are not variations of each other:

- **Highlighting** — the source stays on screen, with headings, emphasis, code spans and fences
  styled in place. Still the bytes, still editable, still a caret at a byte offset.
- **Rendering** — the source is replaced by its result. Headings sized, lists indented, tables laid
  out, links clickable, `**` invisible.

## Decisions at a glance

- **Which one first** — highlighting in the raw view (§1A). It fits the existing surface with no new
  dependency and no new guarantees to defend. Rendering is a second, separate view.
- **Where rendering lives** — its own document view registered in `DocumentViewCatalog`, never the
  raw view (§2). The raw view stays the byte truth.
- **Who parses** — Markdig, not us (§3). CommonMark is not a weekend parser.
- **Who renders** — our own block renderer over the virtualized list pattern, with
  `Markdown.Avalonia` as the shortcut if a size gate is acceptable (§4).
- **How a markdown file is recognised** — extension first, then a corroborating content heuristic
  that demands structure rather than a single `#` (§5).
- **What happens to huge markdown** — a size gate, and a clear reason why "parse only what is on
  screen" is not the easy answer it looks like (§6).

## 1. Highlighting, in the raw view

### A — Style the decoded row text (recommended first step)

`RawTextSurface` already decodes each visible row and draws it as one `TextLayout`, with find
matches painted behind the glyphs (`DrawHighlights`). Markdown highlighting is the same shape of
work: classify spans within a row's text and draw them with different brushes or weights.

What it keeps, all of which rendering gives up: fixed 22px rows, so virtualization stays the
arithmetic it is today (`RowCount * RowHeight` for the extent, `offset.Y / RowHeight` for the
visible range); the caret's exact character-to-byte map (`RawRowDecoder`); editing; and the status
gutter's answers.

The one real problem is that a row cannot be classified on its own. Whether a line is code depends
on a fence that may be thousands of rows above it, and the raw view deliberately realizes only what
is on screen. The existing index already solves the identical problem for line numbers: the scan
stores a little state at every 64th row (`RawSegmentIndex.AnchorStride`, `AppendAnchor`), including
whether that row starts a line, and `GetRowInfo` replays the rules forward from the nearest anchor.
An "inside a fenced block" bit belongs in exactly that state — one bit per 64 rows, not per row, so
the index's ~16 bytes per row is untouched.

Cost: small. No package, no new view, no new memory story.

### B — Style it in a separate pass over the whole file

Parse the document once, keep a span table, look spans up per row. Rejected: the span table is
proportional to the file, which is the thing this app never does. The anchor-replay approach gives
the same answer without holding anything.

## 2. Rendering, and why not in the raw view

The raw view cannot render markdown, for three reasons that are all load-bearing rather than
incidental.

**Virtualization is arithmetic, and rendering makes it measurement.** Every raw row is exactly
`RawTextSurface.RowHeight` tall because rows are byte-capped, so the visible range follows from the
scroll offset without measuring anything, and a 4GB file scrolls for nothing. Rendered markdown has
variable-height blocks — a heading, a fenced block, a table, an image — so the extent becomes a sum
of measured heights, and scrolling becomes estimate-and-correct. That is not a tweak to the surface;
it is most of the surface.

**Rows are byte segments, not semantic units.** A row is "up to the wrap width, break at a newline"
(`RawRowBoundary.Next`). A rendered block spans many rows, and a table spans many blocks. Nothing in
the row model survives contact with block structure.

**The caret is a byte offset, and rendered output has no byte offsets.** `RawRowDecoder` exists to
map a character index back to the byte it came from, which is what makes selection, copy and editing
correct. In rendered output `**bold**` has four bytes that draw nothing, and an image has a
kilobyte of URL that draws one picture. A caret plus rendering in one surface is WYSIWYG editing — a
different project with a different risk profile.

So rendering is a sibling view, registered in `DocumentViewCatalog.Registrations` next to JSON,
NDJSON, CSV and raw, and reachable from the same view switcher. The raw view remains what it is:
what the bytes actually say. The two coexist the way the JSON tree and the raw view already do, and
switching between them is a feature rather than a compromise.

## 3. Parsing: a package, not us

CommonMark is a specification with a ~650-case conformance suite, and the cases that bite are not
exotic: lazy paragraph continuation, setext versus ATX headings, emphasis precedence with mixed
`*` and `_`, tabs in list indentation. A hand-rolled parser is wrong in ways users notice against
GitHub's rendering of the same file.

**Markdig** is the standard .NET answer: BSD-2-Clause, no dependencies, CommonMark-compliant, with
tables, footnotes and task lists as opt-in extensions. Its API parses a `string` into an AST, which
is the one thing to be careful about here — see §6.

The alternative, writing a small subset parser (headings, lists, code fences, emphasis, links),
is defensible *for highlighting* (§1), where being approximate costs a wrong colour rather than
wrong content. It is not defensible for rendering.

## 4. Rendering to Avalonia

### A — `Markdown.Avalonia`

MIT, turns a markdown string into an Avalonia control tree. Fastest route to something real.
It builds the whole tree with no virtualization, so its memory and layout time are proportional to
the document. Acceptable behind a size gate, wrong as the long-term answer for this app.

### B — Our own renderer over the AST (recommended)

Markdig for the AST, then map top-level blocks to row view models and reuse the virtualized list
pattern the app already uses for every other view. Blocks measure individually, which the app has
no existing machinery for (every current list is fixed-height), so this is where the real work is.

The honest sequencing is A then B if a preview is wanted soon, or B directly if it is wanted once.

## 5. Recognising a markdown file

`FileTypeDetector.DetectFileType` currently decides structurally: first non-whitespace character,
then a second-line check for the JSON/NDJSON split, then delimiter counting. Markdown has no such
tell — **every text file is valid markdown**, so detection can only ever be a guess about intent.

**Extension first.** `.md` and `.markdown` are decisive and cost nothing. This is the opposite of
how the rest of the detector works, and it is right here: JSON in a `.txt` file is still JSON,
while a text file with a `#` in it is usually not markdown.

**Then a content heuristic, for files without the extension.** A single `#` is far too weak — the
first line of a shell script, a Python file, a YAML config, a `.gitignore`, an INI file or a
Makefile all start `#`. The signal has to be *structure*, and more than one kind of it. Suggested
rules, all evaluated over a bounded prefix (the detector's existing `PreflightScanLimit` of 1MB is
the natural bound, and in practice the first few dozen lines decide it):

| Signal | Pattern | Strength | Why |
| --- | --- | --- | --- |
| Heading depth varies | both `#` and `##`, or `##` and `###`, each then a space | **Strong** | The discriminating construct. Comment styles are uniform - a file does not switch from `#` to `##` to mean something different - while a document's headings nest by definition |
| `##`+ heading | line starts `##`-`######` then a space | Moderate | Far better than `#`, but not proof: R, Perl and shell all use `##` for decorative section comments |
| `#` heading | line starts `#` then a space | Weak | It is the comment character of shell, Python, YAML, Ruby, Perl, INI files and Makefiles. (`#include`, `#define`, `#!/bin/sh` fail on the required space, so they are free) |
| Fenced code | line starts ` ``` ` or `~~~` | **Strong** | Almost nothing else uses it at line start |
| Setext heading | a text line followed by a line of only `=` or `-` | **Strong** | Common in older READMEs, rare elsewhere |
| Link or image | `[text](url)` or `![alt](src)` | **Strong** | Rare outside markdown |
| Table row | a line containing `\|` with a `\| --- \|` separator under it | **Strong** | Rare outside markdown |
| List item | line starts `-`, `*`, `+` then a space, or `1.` then a space | Weak | Diffs, YAML sequences and changelogs all look like this |
| Emphasis pair | `**text**` or `_text_` within a line | Weak | Multiplication, globs, snake_case, and `/** */` doc comments |

A workable rule: **varying heading depth on its own**, or **one strong signal plus any second
distinct signal**. Weak signals never qualify alone or in pairs - a file whose entire markdown
evidence is a few `#` lines and some `*` bullets is a shell script with a comment header, and it
stays `Unidentified` and opens in the raw view, which is the right failure.

The rule to resist is scoring individual characters. `#` and `**` are the two most common comment
and emphasis markers in the world's source code; what no source file does is *vary its heading
depth*, because its `#`s are not headings at all.

The cost of guessing wrong is genuinely low in both directions, which is what makes a heuristic
acceptable at all: a false positive opens the preview on something that is not markdown, and the
view switcher (`DocumentViewCatalog.Options`) is one click away; a false negative opens the raw
view, which is where the file would have gone anyway. `IsPlausibleFor` should therefore accept
markdown for any text file — there is nothing to reject on.

Worth deciding at the same time, and not before: whether detecting markdown should open the
*preview* or the *highlighted source*. Opening a README to a rendered view is what a reader wants;
opening a file you are editing to a rendered view is not. A per-kind default with a remembered
override is the likely answer, matching how the wrap width is remembered
(`RawWrapWidthPreference`).

## 6. What happens to a huge markdown file

Markdig parses a whole `string`, so a preview costs the file as UTF-16 plus an AST — several times
the file size in memory (estimate; measure before quoting it). That is fine for a README and
disqualifying for the sizes this app exists to open.

**A size gate is the honest v1**: over some threshold, the preview is offered but declines, saying
the file is too large and pointing at the raw view. The app already has this shape of answer
elsewhere — `RawTextExtractor.MaxExtractBytes` refuses a clipboard copy over 64MB rather than
attempting it.

**"Parse only the visible window" is harder than it looks**, and worth writing down so nobody
schedules it cheaply. Block boundaries could be indexed in the background exactly like rows or JSON
tokens are, and blocks parsed on demand. But markdown is not context-free at the document level:
a link reference definition (`[ref]: https://…`) may appear *anywhere* in the file, including after
the link that uses it, so rendering any visible link correctly requires a pre-pass over the whole
document. That pre-pass is cheap and streamable — it is a scan for lines matching a narrow pattern,
which is the kind of thing the app is already good at — but it means windowed parsing is a two-index
feature, not a one-index feature. Footnotes have the same property.

## Outcome

1. **Highlighting in the raw view.** Anchor-carried fence state, span classification per row,
   styled runs in `RawTextSurface`. No package, no new view, nothing new to defend.
2. **Detection.** Extension plus the corroborated heuristic in §5, and a `FileKind.Markdown`.
   Useful on its own: it is what lets the app say "this is markdown" before anything renders it.
3. **A preview view.** Markdig, our own block renderer, behind a size gate, registered in
   `DocumentViewCatalog`. Decide A-then-B versus B-directly (§4) when it is scheduled, not now.
4. **Windowed rendering for large markdown.** Only if anyone actually opens large markdown. Unlike
   giant JSON, it is not a common artefact — this is the step to leave unbuilt unless asked.

Steps 1 and 2 are worth doing regardless of whether 3 ever happens, which is the main argument for
this ordering.

## Related

- [editing-options.md](editing-options.md) — the raw view's row model, caret and byte-offset
  guarantees that §1 preserves and §2 explains cannot survive rendering.
- [roadmap.md](roadmap.md) — where this sits against everything else deferred.
