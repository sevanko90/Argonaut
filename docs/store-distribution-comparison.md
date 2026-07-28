# Windows Store & Mac App Store distribution: what's needed, vs. Velopack/GitHub

Companion to [velopack-auto-update-plan.md](velopack-auto-update-plan.md). That plan
assumes GitHub tags/Releases stay the **source of truth** for every build — this doc
keeps that constraint: the Microsoft Store and Mac App Store are evaluated purely as
*additional distribution channels* built from the same tagged source, not as a
replacement for the GH release pipeline. Nothing here proposes moving CI's source of
truth off GitHub.

The one constraint both stores share and Velopack doesn't: **once an app is
store-distributed, it can't self-update outside the store's own mechanism.**

- Apple, [App Review Guideline 2.5.2](https://developer.apple.com/app-store/review/guidelines/):
  apps "may not download, install, or execute code which introduces or changes
  features or functionality," which Apple enforces against self-updaters.
- Microsoft Store policy requires Store-distributed apps to be installed/updated only
  through the Store, with an explicit carve-out (10.2.9) for apps distributed via a
  plain HTTPS download URL outside the Store — i.e. exactly the Velopack/GitHub path,
  just not applicable once you're *in* the Store.

So a store build must ship with Velopack's `UpdateManager`/`GithubSource` checks
compiled out or no-op'd. GitHub Releases remains the source of truth for the code and
version tag; the store becomes a separate packaging + submission step off that same
tag, with the store's own update delivery downstream of it.

## Microsoft Store (MSIX)

**Packaging.** Argonaut is a normal self-contained Win32/WinExe app (not WinUI/UWP),
so MSIX packaging goes through the classic "Desktop Bridge" path rather than a native
UWP package: publish as today (`dotnet publish -r win-x64 --self-contained`), write an
`AppxManifest.xml` declaring identity/capabilities, then pack with `makeappx.exe` and
sign with `signtool.exe` (or wrap this in a Windows Application Packaging Project if
building from Visual Studio/CI on Windows). This is new CI surface, not a flag on the
existing publish step.

**Sandboxing — the good news.** Desktop Bridge MSIX apps run with package identity but
are **not** put in the UWP AppContainer sandbox by default (`runFullTrust` capability).
Practically, `MemoryMappedFile`/arbitrary file access continues to work the same as
today's portable exe — this is the main reason Windows Store is comparatively cheap to
add: no code changes to `MMapFile.cs` or `RecentFileHistory.cs` should be required.

**Review.** Submissions run through the Windows App Certification Kit + a
store review pass — typically fast (hours to a couple of days), automatable via
Partner Center's submission API (`msstore` CLI or the older StoreBroker PowerShell
module) as a CI step gated on a release tag.

**Cost.** As of the May 2026 Microsoft policy change, both Individual and Company
developer accounts are **free** to register (previously ~$19/$99). No recurring fee.

**Update mechanism.** Store handles updates entirely; Velopack's update check must be
disabled in the MSIX build variant (a compile-time flag is the cleanest way to keep
one codebase serving both channels).

## Mac App Store (MAS)

**Cost/identity.** Requires an Apple Developer Program membership ($99/yr) — the same
enrollment already discussed as an *option* for smoother Velopack macOS updates in the
Velopack plan's notarization wrinkle. If MAS distribution happens, that spend is
shared across both channels; if MAS is skipped, that cost only applies to Velopack if
we choose real notarization there too.

**Sandboxing — the real wrinkle.** MAS requires the `com.apple.security.app-sandbox`
entitlement; today's ad-hoc-signed, non-sandboxed `.app` (see
[scripts/package-macos.sh](../scripts/package-macos.sh)) does not have this. Concretely
for this codebase:

- File opening today goes through `StorageProvider.OpenFilePickerAsync` and OS drag-
  and-drop ([Argonaut/Shell/MainWindow.axaml.cs:211](../Argonaut/Shell/MainWindow.axaml.cs)) —
  both are sandbox-exempt (the OS grants temporary read access for files chosen
  through its own picker/drop APIs), so **first open of a file needs no code change.**
- **Recent files do need a code change.** [RecentFileHistory.cs](../Argonaut/Infrastructure/RecentFileHistory.cs)
  persists plain path strings. Under App Sandbox, a stored path with no accompanying
  grant gives no access on relaunch — reopening a recent file would silently fail.
  This needs security-scoped bookmarks (`NSURL` bookmark data, `startAccessingSecurityScopedResource`)
  persisted instead of/alongside the plain path, resolved back into an access grant
  each time a recent entry is reopened. This is a real, non-trivial implementation
  item, not just a build-config change.
- `MMapFile`/`Utf8JsonReader` etc. operate on whatever `SafeFileHandle`/stream the
  sandboxed access grant already opened, so no changes needed once the file handle is
  legitimately obtained — the sandbox constraint is entirely about *how the app is
  allowed to obtain the handle*, not how it reads the file afterward.

**Packaging & submission.** Build via Xcode archive-and-upload or command-line
equivalent (`xcrun altool`/`notarytool`/Transporter, tooling has shifted over recent
Xcode versions — pin down the current command during implementation rather than
assuming) targeting a distinct **Mac App Store** signing identity/provisioning profile,
separate from the Developer ID identity used for Velopack's GitHub-distributed build.
Running two macOS signing identities in CI means two sets of secrets to manage.

**Review & cadence.** App Review typically runs 24–48h per submission, sometimes
longer, and can reject for guideline issues. This decouples the MAS release cadence
from the GH tag cadence — cutting a GH release does not mean the Store build is live;
it means a submission has started that may sit in review for a day or more, unlike
Velopack where publishing the GH release *is* the release.

**Update mechanism.** Same constraint as Windows Store — Velopack's self-update must
be disabled in the MAS build variant; the App Store's own update mechanism takes over
entirely for that distribution.

## Comparison summary

| | Velopack + GitHub Releases | Microsoft Store (MSIX) | Mac App Store |
|---|---|---|---|
| Source of truth | GH tag/release (this stays true regardless) | Same GH tag, packaged downstream | Same GH tag, packaged downstream |
| Code changes required | None beyond the update plumbing itself | None expected (full-trust Desktop Bridge) | Real: sandbox entitlements + security-scoped bookmarks for recent files |
| New signing identity | Optional (Developer ID improves macOS UX) | Store-managed | Required, separate from Developer ID |
| Monetary cost | $0 (or $99/yr Apple if adding notarization) | $0 (free as of 2026) | $99/yr Apple Developer Program |
| Release latency | Immediate — publish = live | Hours–days (store review) | Hours–days, sometimes longer (App Review) |
| Update delivery | Self-managed, our control, deltas on Windows | Store's own mechanism | Store's own mechanism |
| First-run trust friction for user | SmartScreen/Gatekeeper warnings (mitigated by signing) | None — Store apps are pre-trusted | None — Store apps are pre-trusted |
| Ongoing maintenance | One pipeline (already planned) | One more CI leg + submission step | One more CI leg + submission step + entitlement upkeep |

## Recommendation

Treat both stores as optional, later add-ons rather than replacements for the
Velopack/GitHub pipeline, which should stay the default channel: it has zero review
latency, needs no sandboxing rework, and is already the planned source of truth.

- **Microsoft Store is comparatively cheap to add** if there's a reason to want it
  (discoverability, org policies that only allow Store-installed software) — no
  sandbox rework, no recurring cost, and a fairly light CI addition.
- **Mac App Store is the more expensive of the two** — it requires the security-scoped
  bookmark work for recent files, a second signing identity, a recurring $99/yr, and
  decouples release cadence from GH tagging via App Review turnaround. Worth doing
  only if there's a concrete reason (e.g. users specifically expect to find/update the
  app through the Mac App Store) rather than by default.

Neither store submission should block or be sequenced ahead of the Velopack/GitHub
plan — they're independent, later work streams building on the same source-of-truth
pipeline once it exists.
