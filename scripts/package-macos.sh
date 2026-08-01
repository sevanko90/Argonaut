#!/usr/bin/env bash
set -euo pipefail

# Publishes Argonaut and packs it into a Velopack release for macOS: a portable .app bundle,
# a signed installer .pkg, and a .nupkg update payload (see docs/velopack-auto-update-plan.md).
# `vpk pack` does its own .app assembly (Info.plist, icon, ad-hoc signing) - we no longer
# hand-assemble the bundle ourselves.
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

echo "Packing Velopack release $PACK_VERSION for $RID..."
rm -rf "$VELOPACK_OUT_DIR"
vpk pack \
    --packId "$APP_NAME" \
    --packVersion "$PACK_VERSION" \
    --packDir "$PUBLISH_DIR" \
    --mainExe "$APP_NAME" \
    --icon "$ROOT_DIR/Argonaut/Assets/Icon/argonaut.icns" \
    --plist "$ROOT_DIR/Argonaut/Info.plist" \
    --delta None \
    --outputDir "$VELOPACK_OUT_DIR" \
    -r "$RID"

# Also unpack the portable .app next to Velopack's own outputs, so local/dev runs keep the
# double-click-to-launch convenience the old hand-assembled bundle had.
echo "Extracting portable .app to $DIST_DIR/$APP_NAME.app..."
rm -rf "$DIST_DIR/$APP_NAME.app"
ditto -x -k "$VELOPACK_OUT_DIR/$APP_NAME-osx-Portable.zip" "$DIST_DIR"

echo "Done: $VELOPACK_OUT_DIR (installer, .nupkg, release feed) and $DIST_DIR/$APP_NAME.app"
