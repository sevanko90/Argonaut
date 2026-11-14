# Opening HTTP sources

Goal: open the response to an HTTP request as a document - shown in whichever view its content
calls for, like a file - from a request pane that stays above it to tweak and send again. The
request pane is the start of a request manager (headers, folders, history), so it is built as a
feature of its own from the first step.

Builds on the source locators (`docs/source-locators-plan.md`): a request is a `SourceLocator`, a
response is an origin, and a saved request is a saved source.

## Decided

- **New request** sits beside Open file, Paste and New document in the empty state, and on the
  toolbar with a shortcut. It opens the **request pane** docked at the top of the window, with an
  empty document area below ("Send to load the response").
- **The request pane**: a method dropdown at the start of the URL (GET, POST), the URL box, and
  under it a key/value list of the URL's query arguments with add and remove. The URL and the list
  stay in step both ways: typing or pasting a URL refills the list; adding, removing or editing a
  row rewrites the URL's query. Order, repeated keys, value-less flags (`?debug`) and the fragment
  survive the round trip; the list shows values decoded, the URL keeps them encoded.
- **POST has a body**, edited in a hosted raw editor - a nested `RawViewModel` over the body's
  bytes, as New document runs one - so the body gets the editor's colouring, undo and line
  endings. No toolbar of its own at first.
- **The response is an ordinary document.** Send downloads it and opens it below the pane by
  content detection, as a file opens; the view switcher, find, the tree, the array table, schema
  matching and Save As all apply unchanged. Sending again replaces the document and keeps the
  pane. The pane collapses to give the response the full height.
- **The pane belongs to the shell**, shown while the current input came from a request (or New
  request was chosen). Opening a file, pasting or New document closes it.
- **A download shows progress** on the progress board - by bytes against `Content-Length`, or a
  running byte count when the server sends none - with Stop, which cancels it and leaves the
  previous response (if any) as it was.
- **A request is a saved source** - method, URL and body, sealed with the rest - with the "http"
  pill. Two POSTs to one URL with different bodies are two entries.
- **The last response is kept** on disk, beside the request it answered: closing the document
  keeps both. Reopening the saved request opens the kept response at once - an ordinary mapped
  file, however large - with the pane reading "Response from 14 Nov, 14:32 - Send to refresh". A
  request with no kept response sends on reopen.
- **Kept responses are plain files** in the app's own data folder, readable by the user only -
  the same protection a download's temp file has - with a "Clear kept responses" action. In the
  App Store build the folder is inside the app's container, which the sandbox always allows.
- **Budget: 1 GB in total.** Only the latest response per saved request is kept. Over budget, the
  oldest responses go first, unpinned requests' before pinned ones'. A response larger than the
  whole budget is not kept, and the pane says so. A request leaving the saved list takes its
  response with it; a sweep at startup deletes files no saved request points to.
- **The response is kept as received.** Editing it in the raw view does not change what is kept -
  the response's locator is a request, not a file, so saving is Save As, as for a paste.

## Design

### Request model and query strings (`Engine/Http`)

- `HttpRequestSpec`: `Method` (`Get`/`Post`), `Url` (absolute http/https), `Body` (bytes, POST
  only). Immutable; the pane edits a draft and builds one to send.
- `UrlQuery`: the query as an ordered list of `(Key, Value?)` - `Value` null for a bare flag -
  parsed from and formatted into a URL. Parsing is lossless for what the list can show; formatting
  re-encodes only what needs it, so a URL the user typed is not rewritten until they edit the
  list. The base (scheme, host, path) and fragment are carried through untouched.

### Locator (`Engine/Http`)

`HttpLocator(HttpRequestSpec)`, a `SourceLocator` beside the request it wraps (in `Engine/Http`,
so `Engine/Bytes` does not reach up into requests). `DefaultName` is the URL's last path segment
and host; `DisplayText` the method and URL; `Identity` the method, the URL with scheme and host
lowercased and the fragment dropped, and for a POST a hash of the body.

### Fetching and the response origin (`Engine/Http`)

- `IHttpFetcher` - one injected wrapper over a shared `HttpClient`, so tests substitute responses.
  It streams the body to a stream it is given, reporting bytes as they arrive and the total when
  `Content-Length` says it, and honours cancellation. Status codes other than 2xx are a failure
  with the status line, shown as an open failure.
- The download writes to a temp file in the response folder, under a progress-board entry with
  Stop. When it completes, the file is the response: `HttpResponseOrigin` opens it the way
  `FileByteOrigin` does (mapped, settled from birth) but reports the request's `HttpLocator`, so
  saving is Save As and the saved source is the request.
- Shown once complete, not as it arrives, in the first version: every view reads whole ranges
  (`RequireContiguous`), and a growing response needs a mapping pre-sized to `Content-Length`
  whose written extent is reported separately (see CLAUDE.md on `AvailableLength`). That is a
  later step, and only for responses that state their length.

### Kept responses (`Engine/Http`, folder from the composition root)

`ResponseStore` over the response folder: keeps a finished download for a saved source's id
(a rename into place, never a copy), finds it again, removes it, enforces the budget, and
sweeps orphans. The saved source records which file and when it was fetched. The folder path is
handed down from `App` (`AppDataPaths`), never built here.

### Saved sources (`Shell/Sources`)

`SavedSource` gains what an HTTP entry needs - method, body, and the kept response's file and
time - as settable properties. The shell's reopen gains its second branch, so the `ISourceOpener`
seam the first plan deferred is introduced here: a file opens through `IFileAccess`, a request
through the response store or a fresh send.

### The request pane (`Features/Requests`)

A feature with no document view of its own:
- `RequestEditorViewModel` - method, URL text, the argument rows, the body editor, Send/Stop, the
  status line ("Response from …"), collapsed state.
- `QueryArgumentRowViewModel` - one key/value row.
- `RequestBodyEditor` - hosts the nested `RawViewModel` and gives back the body bytes.
- `RequestEditorView` - the pane.
- Sending goes through an injected `IRequestSender` the shell implements, so the feature never
  reaches into the shell.

The shell owns one `RequestEditorViewModel` while the input is a request, docks the view above
the document area, and opens what Send produces through `OpenOriginAsync`.

## Steps

1. [x] `UrlQuery` and `HttpRequestSpec`. Tests: round trips (order, duplicates, flags, encoding,
   fragment), editing one argument rewrites only the query.
2. [x] `HttpLocator`; identity, names, saved-source storage of method and body. Tests.
3. [ ] `IHttpFetcher` and the download to a temp file with progress and Stop; `HttpResponseOrigin`.
   Tests against a fake fetcher: progress with and without a length, a cancelled download leaves
   nothing, a non-2xx status fails with its status line.
4. [ ] The request pane: URL, method, argument list kept in step with the URL, body editor. Look
   from screenshots. Tests on the view model's two-way sync.
5. [ ] Shell: New request, the docked pane, Send opens the response, sending again, collapsing,
   other inputs closing the pane.
6. [ ] `ResponseStore` and the budget; closing keeps the response; reopening a saved request
   opens it with its age; Clear kept responses; the startup sweep. Tests for eviction order, the
   over-budget response, removal with its request, orphans.
7. [ ] Architecture doc, CLAUDE.md where origins are described, roadmap.

## Not in this plan

- Headers, authentication, other methods, request folders and history - the request manager.
- Showing a response as it downloads (a pre-sized mapping when `Content-Length` is known).
- Following a response's own links, pagination.
