# Plan: Auto-update from GitHub Releases via Velopack

## Goal

Argonaut currently ships as a portable, self-contained single-file publish per RID
(win-x64, osx-arm64, linux-x64), zipped and attached to a GitHub Release by
[.github/workflows/publish.yml](../.github/workflows/publish.yml). There is no update
mechanism today — users manually re-download.

This plan adds in-app auto-update using [Velopack](https://velopack.io), sourcing
updates directly from GitHub Releases, for **Windows and macOS**. Linux is left on the
current manual-zip flow (see [Linux](#linux-not-in-scope)).

## Why Velopack

- Ships a `GithubSource` update source out of the box — no update server to host.
- Handles the "can't overwrite my own running exe" problem on Windows and the
  "replace an app bundle that's currently executing" problem on macOS.
- Produces delta packages on Windows (Zstandard binary diffs) so repeat updates are
  small; macOS is full-package-per-update (no deltas — see wrinkles below).
- Actively maintained successor to Squirrel.Windows/Clowd.Squirrel, with real macOS and
  Linux packagers, not just a Windows-only tool bolted onto other platforms.

## How it fits together

1. **Build**: `dotnet publish` produces the app as today (self-contained, per-RID).
2. **Pack**: the `vpk` CLI takes that publish folder and produces, per RID:
   - An installer (`Setup.exe` on Windows; a `.pkg`/portable `.app` zip on macOS)
   - A `.nupkg` (the "full package" for that version, used both for fresh installs
     and as the update payload)
   - A `releases.{channel}.json` feed file describing available versions
3. **Publish**: all of the above are uploaded as assets on the same GitHub Release
   (alongside the existing zip, or replacing it).
4. **Runtime**: on startup, `VelopackApp.Build().Run()` handles any pending
   install/update completion (must run before anything else touches Avalonia). Later,
   an `UpdateManager` backed by `GithubSource` checks the release feed, downloads the
   new `.nupkg` if present, and applies it (Windows: swaps files via a helper process
   on next launch; macOS: downloads and stages a new `.app`, replacing the bundle on
   restart).

## Scope of changes

- `Argonaut/Argonaut.csproj` — add `Velopack` NuGet package.
- `Argonaut/Program.cs` — bootstrap `VelopackApp.Build().Run()` first thing in `Main`.
- New `Argonaut/Infrastructure/UpdateService.cs` — wraps `UpdateManager`, exposes
  check/download/apply as an async API the UI can call, reports progress and errors.
- `Argonaut/Shell/MainWindow.axaml(.cs)` — a small "Check for Updates" affordance
  (toolbar icon button, matching the existing `ThemeToggleButton`/`FontToggleButton`
  style) plus wiring for toast/confirm prompts.
- `scripts/package-macos.sh` — replaced or supplemented by a `vpk pack` invocation for
  macOS (see [macOS wrinkles](#macos-wrinkles)).
- `.github/workflows/publish.yml` — add a `vpk pack` step per OS and upload the
  installer + `.nupkg` + release feed as release assets instead of (or alongside) the
  raw zip.
- `docs/` — this plan; update `README.md` once shipped to describe the update
  mechanism and the one-time manual-update requirement for existing portable users.

## Windows wrinkles

1. **Install model changes.** Velopack on Windows follows the Squirrel model: it
   installs per-user into `%LocalAppData%\Argonaut`, creates Start Menu shortcuts, and
   writes an `Update.exe` alongside the app that both the installer and the app itself
   use to apply future updates. This is a structural change from "download a zip,
   run the exe from wherever" — **no admin rights required**, which is good, but it
   means the app's install location is no longer wherever the user put the zip.
2. **PublishSingleFile compatibility.** Velopack's Windows packager is generally fine
   with `PublishSingleFile=true` output (it treats the single exe as one file for
   delta purposes), but this needs to be verified against Velopack's current version
   during implementation — pin down in a spike before committing the CI change.
3. **Existing portable users don't auto-update.** Anyone who downloaded a prior
   plain-zip release has no `Update.exe` and no installed-app registration, so the new
   auto-update mechanism can't reach them. They need to manually download the first
   Velopack-based `Setup.exe` once; from that point on they're on the auto-update
   train. This needs a one-time call-out in the release notes / README.
4. **Unsigned binary = SmartScreen.** We don't currently code-sign on Windows, so
   `Setup.exe` will still trigger a SmartScreen "unrecognized publisher" warning on
   first install — no worse than today's unsigned exe, but worth noting since it's
   the same friction, just moved to installer time instead of first-run time. Not
   blocking for this plan; a future concern if we want a smoother first-run.
5. **Relaunch after apply.** `ApplyUpdatesAndRestart` (or
   `WaitExitThenApplyUpdates` if we want to defer to app-exit) needs the app to have
   no unsaved state that would be lost — check `MainWindowViewModel` for anything that
   should prompt before an update-triggered restart (open unsaved views, in-progress
   indexing). Given Argonaut is a read-only viewer this is likely a non-issue, but
   confirm no background indexing is left mid-flight when a restart is triggered.

## macOS wrinkles

1. **Packaging model differs from today's script.** `scripts/package-macos.sh`
   currently hand-assembles the `.app` bundle (copies publish output into
   `Contents/MacOS`, copies `Info.plist` and the `.icns`, ad-hoc signs). `vpk pack`
   on macOS wants to do this assembly itself: point `--packDir` at the publish output,
   `--mainExe` at the entry executable, `--icon` at the icon, and optionally `--plist`
   at a custom `Info.plist` (or let `vpk` generate one from `--bundleId` etc.). The
   existing `Info.plist` bundle identifier (`com.SevanConsulting.Argonaut`) and
   `CFBundleDocumentTypes` (JSON/NDJSON/CSV/TSV file associations) need to be
   preserved — pass the existing `Info.plist` via `--plist` rather than letting `vpk`
   generate a bare-bones one, so file-type associations survive.
2. **Code signing / notarization is the real wrinkle.** Today we ad-hoc sign
   (`codesign --sign -`) purely to satisfy Apple Silicon's "must have *a*
   signature" kernel requirement — it does not clear Gatekeeper's quarantine
   warning, so first-run still needs a right-click-Open. That's tolerable for a
   manual download, but for *auto-update* specifically:
   - Velopack's macOS packager supports proper signing and notarization via
     `--signAppIdentity`, `--signInstallIdentity`, `--signEntitlements`, and
     `--notaryProfile` (an `xcrun notarytool` credentials profile). Without a real
     Apple Developer ID and notarization, updates downloaded and applied in the
     background are more likely to hit Gatekeeper friction than the current
     manual-download flow does, because there's no user-driven "right-click > Open"
     moment to clear the quarantine bit on the replacement bundle.
   - **This needs a decision**: either (a) get an Apple Developer ID ($99/yr) and
     wire up `--signAppIdentity`/`--notaryProfile` in CI via secrets, for a smooth
     background update, or (b) ship ad-hoc-signed updates same as today and accept
     that some macOS users may need to manually intervene (re-approve) after an
     update, which partially defeats the point of "auto" update. Recommend (a) if
     this is going to be a real distribution channel; flagging as an open question
     rather than deciding here.
3. **No delta updates on macOS.** Every update downloads the full app package
   (unlike Windows' Zstandard deltas). Fine for Argonaut's size, but worth knowing
   so we don't promise "small incremental updates" cross-platform.
4. **Update application/relaunch.** Velopack macOS updates stage the new `.app` and
   swap it in via a small helper on restart, similar in spirit to Sparkle. Same
   consideration as Windows: no destructive state loss on the restart trigger.
5. **CI matrix already builds `osx-arm64` only** (see `publish.yml`) — no `osx-x64`
   leg exists today, so Intel Mac users are already unsupported; this plan doesn't
   change that scope.

## Linux (not in scope)

Velopack does have a Linux packager (AppImage-based), but the current pipeline's
linux-x64 zip is low priority for this pass. Leave it on the manual-download flow for
now; revisit once Windows/macOS auto-update is proven out.

## In-app UX design

Argonaut already has two reusable primitives worth building on rather than inventing
new UI:

- `ToastService` ([Argonaut/Infrastructure/ToastService.cs](../Argonaut/Infrastructure/ToastService.cs))
  — fire-and-forget, non-blocking status messages, already wired into `MainWindow`.
- `ConfirmDialog` ([Argonaut/Shell/ConfirmDialog.axaml.cs](../Argonaut/Shell/ConfirmDialog.axaml.cs))
  — a simple Yes/No modal (`ConfirmDialog.Show(owner, message, confirmText)`).

Proposed flow, identical on Windows and macOS since it's entirely our own UI —
Velopack itself is silent/background on both platforms once installed (the Windows
`Setup.exe` has its own brief native installer window for the *initial* install only;
there is no OS-provided UI for in-app update checks on either platform):

1. **Background check on startup**, throttled (e.g. at most once per 24h, tracked via
   a small marker file under `AppDataPaths` alongside the existing settings files).
   Silent if no update is found — no toast, no interruption.
2. **Toast on update found**: `ToastService.Show("Update available (v1.4.0) — click to install")`
   or similar, non-blocking, dismissible, consistent with how other background events
   already surface in the app.
3. **Manual trigger**: a "Check for Updates" toolbar icon button (same visual family
   as `ThemeToggleButton`/`FontToggleButton` in `MainWindow.axaml`) for users who want
   to check on demand rather than wait for the background check.
4. **Download progress**: reuse the toast channel for a simple "Downloading update…
   XX%" status (Velopack's `DownloadUpdatesAsync` takes a progress callback); no need
   for a dedicated progress dialog given Argonaut's existing minimal-chrome style.
5. **Restart confirmation**: once downloaded, `ConfirmDialog.Show(owner,
   "Update downloaded. Restart Argonaut now to apply it?", "Restart")`. Yes calls
   `ApplyUpdatesAndRestart`; No leaves the update staged to apply on next natural
   quit (`WaitExitThenApplyUpdates`, or simply re-prompt next launch).
6. **Errors** (offline, GitHub rate limit, corrupt download): a toast, not a dialog —
   auto-update failures shouldn't block the user from using the app they already
   have open.

No menu bar currently exists in `MainWindow.axaml` (it's a toolbar-based shell), so
"Check for Updates" belongs in the toolbar rather than a Help menu, consistent with
the current UI language.

## CI / release changes

`publish.yml` currently: publish → (macOS: assemble `.app`) → zip → attach to release.

Add, per OS leg, after the existing `dotnet publish` step:

```yaml
- name: Pack with Velopack
  run: |
    dotnet tool install -g vpk
    vpk pack --packId Argonaut --packVersion ${{ github.ref_name }} \
      --packDir <publish-dir> --mainExe Argonaut \
      --icon <icon-path> --outputDir dist/velopack
```

with OS-specific flags (macOS: `--bundleId com.SevanConsulting.Argonaut --plist
Argonaut/Info.plist`, plus signing flags once the certificate decision is made).
Then upload `dist/velopack/*` (installer, `.nupkg`, `releases.{channel}.json`) as
release assets via the existing `softprops/action-gh-release` step, keeping the
plain zip too during a transition period so nothing breaks for people scripting
against today's asset names.

**Ordering matters**: the release feed file must reflect the *latest published*
version across all packed releases, and Velopack expects previous `.nupkg`s
available in the output dir when generating deltas — for a GitHub-Releases-as-store
setup this typically means downloading the previous release's assets into the
`vpk pack` output dir before packing, or accepting full-only packages initially and
adding delta generation once this is bedded in. Recommend starting **without deltas**
(`--delta None`) for the first cut to keep the CI change simple, and layering delta
generation on as a follow-up once the basic update loop is proven.

## Rollout phases

1. **Spike** (no CI changes): locally `vpk pack` a Release build on both a Mac and a
   Windows machine/VM, confirm the resulting installer installs, launches, and that a
   second packed version is detected and applied via `UpdateManager` pointed at a
   local file-based `GithubSource`-equivalent (Velopack supports a local/file update
   source for testing without touching real GitHub releases).
2. **Wire up app-side code**: `VelopackApp.Build().Run()`, `UpdateService`, toolbar
   button, toast/dialog flow — testable against the local source from step 1.
3. **CI integration**: add `vpk pack` to `publish.yml`, dual-publish zip + Velopack
   assets for one release cycle.
4. **Cut over**: point `GithubSource` at the real repo, ship a release, verify the
   full loop against the live GitHub Releases feed from a real (non-CI) machine.
5. **Communicate the one-time manual step** to existing users (README + release
   notes): "if you installed Argonaut before v1.x, download this release manually
   once; future updates will be automatic."
6. **(Optional, later)** Apple Developer ID + notarization for a friction-free macOS
   background update; delta package generation for smaller Windows updates; Linux
   AppImage + Velopack packaging.

## Corrections from implementation

A local macOS spike (`vpk pack` against a real publish output) surfaced two inaccuracies in
this plan as originally written:

- **`--bundleId` and `--plist` are mutually exclusive**, not combinable as suggested above in
  [macOS wrinkles](#macos-wrinkles) point 1. Since the existing `Info.plist` already carries
  `CFBundleIdentifier` and `CFBundleDocumentTypes`, the implementation passes `--plist` alone.
- **Ad-hoc signing is automatic**, not something `vpk pack` needs an explicit flag for. Packing
  without `--signAppIdentity`/`--notaryProfile` logs "Package will not be signed or notarized"
  but `codesign -dv` on the resulting bundle shows `flags=0x2(adhoc)` regardless - Velopack
  applies the same ad-hoc signature the old `codesign --sign -` step did, satisfying Apple
  Silicon's "must have *a* signature" requirement without a Developer ID. This matches the
  chosen option (b) in that same wrinkles section (accept manual Gatekeeper approval after an
  update rather than pursuing notarization for this pass).
- **`vpk pack`'s output directory has more in it than the release needs.** Alongside the
  installer and `.nupkg`, it always writes `RELEASES`/`RELEASES-{channel}` (a legacy
  Squirrel-format file, kept only for backward compat with pre-Velopack clients - confirmed
  via Velopack's own source, `GitBase.cs`/`CoreUtil.cs`) and `assets.{channel}.json` (`vpk`'s
  own local bookkeeping for delta generation across repeated packs into the same output
  dir), plus a portable `.zip` that duplicates the plain zip this workflow already attaches.
  `GitBase.GetReleaseFeed` - the code `GithubSource` actually runs on `CheckForUpdatesAsync`
  - only ever fetches `releases.{channel}.json` from the release, then the `.nupkg` it
  references. `publish.yml`'s "Attach Velopack assets to release" step therefore only
  uploads `*.exe`, `*.pkg`, `*.nupkg`, and `releases.*.json`, leaving the rest out of the
  release entirely.

Decisions made for this implementation pass (the plan's open questions, resolved):
release tags must be clean SemVer (`vX.Y.Z`) going forward - both packaging scripts fail fast
on anything else; the plain zip stays alongside Velopack assets for this transition; delta
packages are off (`--delta None`) for both platforms' first cut.

## Open questions

- Apple Developer ID / notarization budget and ownership — needed for the smoothest
  macOS auto-update experience (see [macOS wrinkles](#macos-wrinkles) point 2).
- Whether to keep publishing the plain zip long-term for users who explicitly want a
  portable, no-install build, or fully retire it once Velopack is proven.
- Release cadence/channel naming if we ever want a beta channel (`--channel`) distinct
  from stable — not needed for v1 of this plan.
