#!/usr/bin/env bash
set -euo pipefail

# Publishes Argonaut for the Mac App Store channel (-p:DistributionChannel=AppStore, see
# Argonaut.csproj): no Velopack package, no self-update code, and third-party notices without
# Velopack's section. Assembles dist/appstore/Argonaut.app by hand (vpk does this for the GitHub
# build) and fails if anything inside the bundle still mentions Velopack.
#
# Not yet a submission build: the bundle is unsigned. App Store signing identities, the App
# Sandbox entitlement and productbuild are still to come (see docs/store-distribution-comparison.md).
#
# Usage: scripts/package-macos-appstore.sh [osx-arm64|osx-x64] [version]
#   version must be a clean SemVer (e.g. 1.4.0, an optional leading 'v' is stripped); omitted
#   (or during local/dev runs) it defaults to 0.0.1, matching package-macos.sh.

RID="${1:-osx-arm64}"
RAW_VERSION="${2:-}"
CONFIGURATION="Release"
APP_NAME="Argonaut"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/Argonaut/Argonaut.csproj"
APPSTORE_DIR="$ROOT_DIR/dist/appstore"
PUBLISH_DIR="$APPSTORE_DIR/publish"
APP_DIR="$APPSTORE_DIR/$APP_NAME.app"
ZIP_PATH="$APPSTORE_DIR/$APP_NAME-$RID-appstore.zip"

if [ -z "$RAW_VERSION" ]; then
    VERSION="0.0.1"
else
    VERSION="${RAW_VERSION#v}"
    if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        echo "error: version '$RAW_VERSION' is not a clean SemVer tag (expected vX.Y.Z)." >&2
        exit 1
    fi
fi

# Its own output directory, never the GitHub build's bin/.../publish: a shared publish folder can
# keep a Velopack.dll left behind by the other channel.
rm -rf "$APPSTORE_DIR"

echo "Publishing $APP_NAME for $RID ($CONFIGURATION, App Store channel)..."
dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:InformationalVersion="$VERSION" \
    -p:DistributionChannel=AppStore \
    -o "$PUBLISH_DIR"

echo "Assembling $APP_DIR..."
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
ditto "$PUBLISH_DIR" "$APP_DIR/Contents/MacOS"
cp "$ROOT_DIR/Argonaut/Info.plist" "$APP_DIR/Contents/Info.plist"
cp "$ROOT_DIR/Argonaut/Assets/Icon/Argonaut.icns" "$APP_DIR/Contents/Resources/$APP_NAME.icns"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP_DIR/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP_DIR/Contents/Info.plist"

# Seal the bundle, last, since any change to it afterwards breaks the seal. Apple Silicon refuses to
# launch a bundle with no valid signature - the "is damaged and can't be opened" error the old
# hand-assembled bundle hit (4b7edfa). The linker ad-hoc signs the executable alone, which leaves
# Info.plist and resources unsealed. Ad-hoc (identity "-") is enough to launch a local build; App
# Store signing will replace it.
echo "Ad-hoc signing $APP_DIR..."
codesign --force --deep --sign - "$APP_DIR"
codesign --verify --deep --strict "$APP_DIR"

# The guarantee this channel exists for: nothing Velopack-related in what ships - no assembly
# inside the single-file bundle, no updater, no attribution text.
if grep -rli --binary-files=text velopack "$APP_DIR"; then
    echo "error: the App Store build still mentions Velopack in the files listed above." >&2
    exit 1
fi

ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$ZIP_PATH"

echo "Done: $APP_DIR (ad-hoc signed) and $ZIP_PATH"
