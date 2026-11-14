# Source locators and saved sources

Goal: replace "recent files" with **saved sources** - named, renamable entries that say where a
document came from and how to open it again - before the HTTP source is built. A source is a
file today and a URL next, and later grows into a Postman-style saved-request manager (headers,
folders). The same identity also keys remembered schema bindings, so a URL document can
remember its schema.

## Decided

- **`IByteOrigin` loses `Path`; it gains `Locator`.** A `SourceLocator` describes where the
  bytes came from, and a file's path is one kind of locator - there is no second, parallel
  "path" concept. A document with nowhere to go back to (a paste, an excerpt, a diff side) has a
  null locator. Every feature that touches the file system asks for the file kind
  (`origin.Locator is FileLocator file`) instead of `origin.Path is not null`.
- **macOS uses bookmarks for every file, in every build.** A `FileLocator` carries the file's
  bookmark beside its path on macOS, created when the file is opened and resolved when it is
  reopened - the same code in the GitHub build and the App Store build, with no sandbox-only
  branch. A side benefit: a bookmark follows a file the user moved or renamed. Windows and Linux
  carry no bookmark.
- **No migration.** The new settings blocks have new keys; `recentFiles`, `favouriteFiles` and
  `schemaBindings` are no longer read. Users start with an empty list and no remembered schemas
  once. Few users, and the code would be used once.
- **URLs are kept whole, and the saved store is encrypted.** The saved sources and the schema
  bindings are written to `settings.json` as one sealed (AES-GCM) blob each, so neither a URL's
  query keys nor a file path is readable from the file. The key is generated on first use and
  kept by the OS, outside the app's own folders, so it survives a reinstall:
  - **macOS:** a generic-password item in the user's login Keychain.
  - **Windows:** DPAPI (`ProtectedData`, current-user scope) - no key to store at all; the
    profile is the key.
  - **Linux:** a key file readable only by the user (`0600`) in the config folder - no secret
    service is assumed.
  If the key is missing or the blob does not open (a new machine, a deleted Keychain item), the
  list starts empty - never an error.
- **Entries are renamable.** A new entry is named from its locator (the file name; a URL's last
  path segment and host), and the user can rename it from its row.
- **A pill only on non-file rows** - "http" today.
- **Schema bindings are keyed by locator**, for an explicit choice only (a schema the user picked,
  or "No schema"). A matched schema is never stored, as now, so matching still runs on a URL
  document's data.
- **A source that fails to reopen stays.** The open fails as any open does, and the toast offers
  "Remove from list". Nothing is removed automatically.
- **Pins and recents are one list.** An entry is pinned or not; the cap applies to unpinned
  entries only. The empty state shows the two as it does now.
- **Each entry has an id** (a GUID), so a later hierarchy of folders and saved requests can
  refer to entries without keying on their location.

## Design

### Locators (`Engine/Bytes`, beside the origins)

```
abstract record SourceLocator          // what to open again, and how to name it
  string DefaultName
  string Identity                      // normalised, for keying: never shown
sealed record FileLocator(string Path, byte[]? Bookmark) : SourceLocator
sealed record HttpLocator(Uri Url) : SourceLocator      // added with the HTTP source
```

- `FileLocator.Identity` is the full path, compared case-insensitively on macOS and Windows.
  `HttpLocator.Identity` is the URL with scheme and host lowercased and the fragment dropped.
  Each carries its kind, so a file and a URL never collide.
- `origin.LocationText()` is the display text (the locator's, or the display name), replacing
  the `Path ?? DisplayName` idiom on the views.
- `IByteOrigin.Locator` replaces `Path`. `FileByteOrigin` builds a `FileLocator`;
  `ExcerptByteOrigin`, `MemoryByteOrigin` and a diff side report null.
- The 27 reads of `origin.Path` move to the locator: saving and its `IFileReplacer`, line-ending
  detection, `ByteOriginVersion`, the views' display paths, reveal-in-folder, schema bindings.
  CLAUDE.md's origin section is rewritten to say "a file locator" where it says "Path".

### File access (`Engine/Bytes`, impls in `Shell`)

`IFileAccess`, chosen in the composition root:
- `Describe(path)` → the `FileLocator` for a file the user just picked or dropped, with a
  bookmark where the platform has them.
- `Open(FileLocator)` → a grant (`IDisposable`) plus the path to open now, resolving a bookmark
  (and refreshing a stale one) and starting access; disposing the grant stops it. On Windows
  and Linux the grant does nothing and the path is the locator's own.
- macOS implementation over `NSURL` bookmark data and
  `startAccessingSecurityScopedResource`, by Objective-C runtime calls - the same native route
  the macOS `IFileReplacer` will take. Entitlement for bookmarks on the store build.
- `FileByteOrigin` holds its grant and releases it on dispose, so access lives exactly as long
  as the input does (`AdoptOrigins` already owns that lifetime).

### Sealing (`Engine/Settings`)

`ISettingsSealer` - `string? Seal(ReadOnlySpan<byte>)` / `byte[]? Open(string)` - chosen in the
composition root by `SettingsSealers.ForThisPlatform`: `DpapiSettingsSealer` on Windows, and
`AesGcmSettingsSealer` over an `ISealingKeyStore` elsewhere (`KeychainSealingKeyStore` on macOS,
`FileSealingKeyStore` on Linux). A key that cannot be stored is never used - sealing returns
null and the block is left as it was. The Keychain test runs only with
`ARGONAUT_KEYCHAIN_TESTS=1`, since a CI runner's keychain may be locked. Tests use a fixed-key
sealer. A sealed block is an ordinary settings block holding one sealed string, and the service that
owns it opens it on first read and re-seals it on every change. The store gains a way to drop a
key, so the old unsealed `recentFiles`, `favouriteFiles` and `schemaBindings` are removed from
`settings.json` rather than left in it in plain text.

### Saved sources (`Shell/Sources`)

- `SavedSource` (settable properties): `Id`, `Name`, `Locator`, `IsPinned`, `LastOpened`.
  Serialised with a kind discriminator on the locator.
- `SavedSourceStore` - the injected service over the sealed block: add-or-touch on open (by
  locator identity), rename, pin, remove, clear unpinned. Replaces `RecentFileHistory` and
  `FavouriteFiles`.
- `ISourceOpener` turns a `SavedSource` back into an `IByteOrigin` - a file locator through
  `IFileAccess`, an HTTP locator through the HTTP origin once it exists. The shell records
  whatever locator an opened origin reports and reopens through the opener, with no type checks
  of its own.
- Empty state rows: name, location as the tooltip, the kind pill on non-file rows, pin, and
  rename (inline edit on the name).

### Schema bindings

`SchemaBindings` keys by locator identity instead of path, sealed like the saved sources. Its
schema path stays a path: the schema folders are the app's own, and the store build's schema
folder work is separate.

### Toasts with an action

`ToastService.Show(message)` gains an overload with one action (label + callback), used for
"Remove from list". The toast host draws it as a link button.

## Steps

1. [x] `SourceLocator`/`FileLocator`; `IByteOrigin.Locator` replaces `Path` at every reader;
   CLAUDE.md and architecture.md follow. Behaviour unchanged. Tests: identity and default
   names; existing suite green.
2. [x] `ISettingsSealer` and its three implementations, plus the test sealer. Tests: round
   trip, a tampered or foreign blob opens as nothing, the macOS Keychain key created once and
   reused (macOS-only test), DPAPI on Windows CI.
3. [ ] `SavedSourceStore` replacing recents and favourites, sealed; the shell records locators
   and reopens through `ISourceOpener`. Tests: add-or-touch, cap on unpinned only, rename, pin,
   sealed on disk (no path text in `settings.json`), empty when the blob will not open.
4. [ ] Empty-state rows: rename, pill on non-file rows; toast action and "Remove from list" on
   a failed reopen. Look from a screenshot.
5. [ ] `IFileAccess` with the macOS bookmark implementation; `FileByteOrigin` holds the grant.
   Tests: describe/open round trip on a temp file, a moved file found through its bookmark
   (macOS), the grant released on dispose.
6. [ ] Schema bindings keyed by locator identity and sealed. Tests: an explicit choice
   remembered across reopen of the same locator, a matched one still not stored.
7. [ ] Store doc and roadmap updated: recent-file and remembered-schema bookmarks done; the HTTP
   source plugs in as an `HttpLocator` plus an opener branch.

## Not in this plan

- The HTTP origin itself - next, on top of this.
- Headers, folders, request editing (the saved-request manager).
- The macOS `IFileReplacer` over `NSFileManager`, and the schema folder's store-build rework.
