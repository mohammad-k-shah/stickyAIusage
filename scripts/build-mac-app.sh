#!/usr/bin/env bash
# Builds "Sticky AI Usage.app" for macOS: a self-contained publish (no .NET
# install needed to run it) wrapped in an app bundle that lives in the menu
# bar (LSUIElement - no Dock icon), ad-hoc signed so Apple Silicon runs it.
#
#   scripts/build-mac-app.sh            # Apple Silicon (osx-arm64)
#   scripts/build-mac-app.sh osx-x64    # Intel Macs
#
# Output: "artifacts/mac/Sticky AI Usage.app" (and a .zip next to it). The
# bundle's file name is what Finder, Spotlight and Launchpad show.
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/mac"
APP="$OUT/Sticky AI Usage.app"
PUBLISH="$OUT/publish-$RID"
VERSION="${VERSION:-0.2.0}"

rm -rf "$APP" "$OUT/StickyAIusage.app" "$PUBLISH"   # (StickyAIusage.app: the old bundle name)
dotnet publish "$ROOT/src/StickyAIusage.Mac/StickyAIusage.Mac.csproj" -c Release -r "$RID" \
  --self-contained true -p:UseAppHost=true -o "$PUBLISH"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH"/. "$APP/Contents/MacOS/"

# App icon: .icns from the same artwork as the Windows icon.
ICONSET="$OUT/AppIcon.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
SRC="$ROOT/src/StickyAIusage.Mac/Assets/app.png"
for size in 16 32 128 256; do
  sips -z $size $size "$SRC" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Sticky AI Usage</string>
    <key>CFBundleDisplayName</key><string>Sticky AI Usage</string>
    <key>CFBundleIdentifier</key><string>com.stickyaiusage.widget</string>
    <key>CFBundleExecutable</key><string>StickyAIusage</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP"
(cd "$OUT" && rm -f "StickyAIusage-$RID.zip" && ditto -c -k --keepParent "Sticky AI Usage.app" "StickyAIusage-$RID.zip")
echo "Built $APP"
