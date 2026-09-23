# Saving — what remains

Save and Save As are built for Windows, Linux and unsandboxed macOS: `RawViewModel.SaveAsync`
drives the sequence, `SiblingFileReplacer` is the `IFileReplacer` behind it, and the shell asks
before anything would drop unsaved edits. The code comments carry the reasoning. What is left is
a second `IFileReplacer` for macOS, and the rules it has to meet.

## The macOS replacer (`NSFileManager`)

Needed outright for a Mac App Store build, and preferable on macOS even without one.

- **Why the sandbox needs it.** A file the user picked or dropped grants access to that file, not
  its folder, so `SiblingFileReplacer`'s stage beside the destination is denied. Use what
  `NSDocument` does: `NSFileManager.URLForDirectory(NSItemReplacementDirectory,
  appropriateForURL: destination)` for a staging directory the sandbox permits on the right
  volume, then `replaceItemAtURL:withItemAtURL:`, inside `startAccessingSecurityScopedResource`
  and ideally an `NSFileCoordinator` write so sync clients (iCloud Drive, Dropbox) see one change.
- **Why it is better unsandboxed too.** `rename(2)` installs a new file, so extended attributes
  (Finder tags, quarantine) and ACLs are lost today; `SiblingFileReplacer` only carries the mode
  bits across. `replaceItemAtURL` keeps the rest, and one macOS path is cheaper than two.
- **What else it needs.** Native interop (Objective-C runtime calls or a small shim - no .NET API
  reaches it), the `com.apple.security.files.user-selected.read-write` entitlement, and
  recent-file bookmarks created without the read-only option. The wider sandbox picture is in
  [store-distribution-comparison.md](store-distribution-comparison.md).
- **Where it plugs in.** `MainWindowViewModel` takes an `IFileReplacer`; choose it by platform
  there. `Stage` takes the destination `IByteOrigin` rather than a path so a security-scoped grant
  can travel with it. Save As currently builds a `FileByteOrigin` from the picked path; under the
  sandbox it has to carry the picker's grant instead.

## Rules every `IFileReplacer` must meet

`SiblingFileReplacer` meets all of these; a new implementation must too.

- **Same volume, or it is not atomic.** Stage beside the destination, or in a directory the OS
  guarantees is on its volume - never `Path.GetTempPath()`.
- **Durable before the swap.** `Seal()` flushes to stable storage, and on macOS that means
  `F_FULLFSYNC`, not plain `fsync`. It runs on the background copy, so `Commit()` is only the swap.
- **Destination untouched unless the commit succeeded.** `RawViewModel` depends on it: a failed
  commit remaps the original and the edits carry on over it.
- **Replace a symlink's target, not the link.** Hard links are broken by any rename-based save;
  accepted.
- **Fail before writing** when the volume lacks room for the whole new file.
- **Never delete a stage that may be the only copy.** `KeepStagedContent()` is called when a
  failed commit also left the original unreadable.
