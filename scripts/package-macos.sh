#!/usr/bin/env bash
set -euo pipefail

# Publishes Argonaut and packs it into a Velopack release for macOS: a portable .app bundle,
# a signed installer .pkg, and a .nupkg update payload (see docs/velopack-auto-update-plan.md).
# The .app is assembled here (Info.plist, icon, provisioning profile) and handed to `vpk pack`,
# which adds its updater, signs and notarises it: vpk can build a bundle itself, but has no way
# to put a provisioning profile in one.
#
# The profile is what lets the app use the data-protection Keychain (see
# KeychainSealingKeyStore): its keychain-access-groups entitlement is restricted, and macOS kills
# at launch an app that claims it without a matching embedded profile. So the entitlements are
# only added when ARGONAUT_PROVISION_PROFILE names the profile - the release workflow decodes it
# from a secret; a local run without it builds an app that falls back to the login Keychain. A
# tagged release refuses to pack without it.
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
if [ -n "${ARGONAUT_PROVISION_PROFILE:-}" ]; then
    echo "Embedding provisioning profile and the Keychain entitlements..."
    cp "$ARGONAUT_PROVISION_PROFILE" "$APP_BUNDLE/Contents/embedded.provisionprofile"
    ENTITLEMENTS="$APP_STAGE_DIR/entitlements.entitlements"
    cp "$ROOT_DIR/Argonaut/entitlements.entitlements" "$ENTITLEMENTS"
    APP_IDENTIFIER="$TEAM_ID.$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$APP_BUNDLE/Contents/Info.plist")"
    /usr/libexec/PlistBuddy \
        -c "Add :com.apple.application-identifier string $APP_IDENTIFIER" \
        -c "Add :com.apple.developer.team-identifier string $TEAM_ID" \
        -c "Add :keychain-access-groups array" \
        -c "Add :keychain-access-groups:0 string $APP_IDENTIFIER" \
        "$ENTITLEMENTS"
elif [ -n "$RAW_VERSION" ]; then
    echo "error: a release must embed the provisioning profile - set ARGONAUT_PROVISION_PROFILE." >&2
    exit 1
else
    echo "No ARGONAUT_PROVISION_PROFILE: packing without the Keychain entitlements."
fi

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

echo "Done: $VELOPACK_OUT_DIR (installer, .nupkg, release feed) and $DIST_DIR/$APP_NAME.app"
