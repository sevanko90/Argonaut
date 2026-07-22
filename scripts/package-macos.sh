#!/usr/bin/env bash
set -euo pipefail

# Publishes Argonaut and assembles it into a self-contained Argonaut.app bundle so macOS
# reads its name/version from Argonaut/Info.plist instead of showing "Avalonia Application"
# in the menu bar (that only happens for a real .app bundle - dotnet run never produces one).
#
# Usage: scripts/package-macos.sh [osx-arm64|osx-x64]

RID="${1:-osx-arm64}"
VERSION="${2:-}"
CONFIGURATION="Release"
APP_NAME="Argonaut"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/Argonaut/Argonaut.csproj"
PUBLISH_DIR="$ROOT_DIR/Argonaut/bin/$CONFIGURATION/net10.0/$RID/publish"
DIST_DIR="$ROOT_DIR/dist"
BUNDLE_DIR="$DIST_DIR/$APP_NAME.app"

echo "Publishing $APP_NAME for $RID ($CONFIGURATION)..."
# Force a clean publish dir: an incremental `dotnet publish` can skip re-emitting native
# .dylib files (e.g. libSkiaSharp, libHarfBuzzSharp, libAvaloniaNative) alongside the
# single-file executable, silently producing a bundle missing them.
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    ${VERSION:+-p:InformationalVersion="$VERSION"}

echo "Assembling $BUNDLE_DIR..."
rm -rf "$BUNDLE_DIR"
mkdir -p "$BUNDLE_DIR/Contents/MacOS" "$BUNDLE_DIR/Contents/Resources"

cp -R "$PUBLISH_DIR"/. "$BUNDLE_DIR/Contents/MacOS/"
cp "$ROOT_DIR/Argonaut/Info.plist" "$BUNDLE_DIR/Contents/Info.plist"
cp "$ROOT_DIR/Argonaut/Assets/Icon/argonaut.icns" "$BUNDLE_DIR/Contents/Resources/Argonaut.icns"

chmod +x "$BUNDLE_DIR/Contents/MacOS/$APP_NAME"

echo "Done: $BUNDLE_DIR"
