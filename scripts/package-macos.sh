#!/usr/bin/env bash
set -euo pipefail

# Publishes Argonaut and packs it into a Velopack release for macOS: a portable .app bundle,
# a signed installer .pkg, and a .nupkg update payload (see docs/velopack-auto-update-plan.md).
# The .app is assembled here (Info.plist, icon) and handed to `vpk pack`, which adds its updater
# (UpdateMac), signs and notarises it.
#
# vpk signs every binary in the bundle with the one entitlements file, UpdateMac included, so the
# file must hold only entitlements a bare helper may claim. A restricted one (keychain-access-groups,
# application-identifier) needs a provisioning profile, and macOS kills UpdateMac at launch without
# one - updates then quit the app and never apply. The check after packing guards against that.
#
# Usage: scripts/package-macos.sh [osx-arm64|osx-x64] [version]
#   version must be a clean SemVer (e.g. 1.4.0, no leading 'v') when packing for release;
#   omitted (or during local/dev runs) it defaults to 0.0.1 (vpk rejects 0.0.0). Velopack's
#   AutoApplyOnStartup (silently swapping in the highest-versioned .nupkg from its local
#   package cache on launch) is disabled in Program.cs, so a low, stable local version here is
#   safe - it won't get clobbered by a higher-versioned package left in that cache by an
#   earlier local run.

RID="${1:-osx-arm64}"
RAW_VERSION="${2:-}"
CONFIGURATION="Release"
APP_NAME="Argonaut"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/Argonaut/Argonaut.csproj"
PUBLISH_DIR="$ROOT_DIR/Argonaut/bin/$CONFIGURATION/net10.0/$RID/publish"
DIST_DIR="$ROOT_DIR/dist"
VELOPACK_OUT_DIR="$DIST_DIR/velopack"
APP_STAGE_DIR="$ROOT_DIR/Argonaut/bin/$CONFIGURATION/net10.0/$RID/app-stage"
TEAM_ID="5S97A7W8W5"

if [ -z "$RAW_VERSION" ]; then
    PACK_VERSION="0.0.1"
else
    PACK_VERSION="${RAW_VERSION#v}"
    if ! [[ "$PACK_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        echo "error: version '$RAW_VERSION' is not a clean SemVer tag (expected vX.Y.Z)." >&2
        echo "Velopack needs a parseable version per package - retag the release as vX.Y.Z." >&2
        exit 1
    fi
fi

export PATH="$PATH:$HOME/.dotnet/tools"
if ! command -v vpk >/dev/null 2>&1; then
    echo "Installing vpk CLI..."
    dotnet tool install --global vpk --version 1.2.0
fi

echo "Publishing $APP_NAME for $RID ($CONFIGURATION)..." 
echo "Publish to: $PUBLISH_DIR"
# Force a clean publish dir: an incremental `dotnet publish` can skip re-emitting native
# .dylib files (e.g. libSkiaSharp, libHarfBuzzSharp, libAvaloniaNative) alongside the
# single-file executable, silently producing a bundle missing them.
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:InformationalVersion="$PACK_VERSION"

echo "Assembling $APP_NAME.app..."
rm -rf "$APP_STAGE_DIR"
APP_BUNDLE="$APP_STAGE_DIR/$APP_NAME.app"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"
ditto "$PUBLISH_DIR" "$APP_BUNDLE/Contents/MacOS"
cp "$ROOT_DIR/Argonaut/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp "$ROOT_DIR/Argonaut/Assets/Icon/Argonaut.icns" "$APP_BUNDLE/Contents/Resources/Argonaut.icns"

ENTITLEMENTS="$ROOT_DIR/Argonaut/entitlements.entitlements"

echo "Packing Velopack release $PACK_VERSION for $RID..."
rm -rf "$VELOPACK_OUT_DIR"
vpk --legacyConsole pack \
    --signAppIdentity "Developer ID Application: Marc Evans ($TEAM_ID)" \
    --signInstallIdentity "Developer ID Installer: Marc Evans ($TEAM_ID)" \
    --signEntitlements "$ENTITLEMENTS" \
    --notaryProfile "argonaut-notary" \
    --packId "$APP_NAME" \
    --packVersion "$PACK_VERSION" \
    --packDir "$APP_BUNDLE" \
    --mainExe "$APP_NAME" \
    --icon "$ROOT_DIR/Argonaut/Assets/Icon/Argonaut.icns" \
    --delta None \
    --outputDir "$VELOPACK_OUT_DIR" \
    -r "$RID"

# Also unpack the portable .app next to Velopack's own outputs, so local/dev runs keep the
# double-click-to-launch convenience the old hand-assembled bundle had.
echo "Extracting portable .app to $DIST_DIR/$APP_NAME.app..."
rm -rf "$DIST_DIR/$APP_NAME.app"
ditto -x -k "$VELOPACK_OUT_DIR/$APP_NAME-osx-Portable.zip" "$DIST_DIR"

# The updater must be able to start: macOS SIGKILLs (exit 137) a binary whose entitlements need a
# profile it does not have, before it runs a line - which is how every update from 2.3.1 to 2.4.0
# failed silently. Fail the pack instead of shipping that.
echo "Checking the packed updater launches..."
set +e
"$DIST_DIR/$APP_NAME.app/Contents/MacOS/UpdateMac" --help >/dev/null 2>&1
UPDATER_EXIT=$?
set -e
if [ "$UPDATER_EXIT" -ge 128 ]; then
    echo "error: the packed UpdateMac was killed at launch (exit $UPDATER_EXIT) - check its entitlements:" >&2
    codesign -d --entitlements - "$DIST_DIR/$APP_NAME.app/Contents/MacOS/UpdateMac" >&2 || true
    exit 1
fi

echo "Done: $VELOPACK_OUT_DIR (installer, .nupkg, release feed) and $DIST_DIR/$APP_NAME.app"
