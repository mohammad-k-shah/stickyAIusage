#!/usr/bin/env bash
# Signs "Sticky AI Usage.app" and zips it (called by scripts/build-mac-app.sh).
#
#   scripts/sign-mac-app.sh <path to the .app> <path of the zip to write>
#
# Without a Developer ID (the default: every build but a release whose
# repository has the signing secrets) the bundle is ad-hoc signed, so Apple
# Silicon runs it and Gatekeeper asks once. With MAC_SIGN_IDENTITY (the name of
# a "Developer ID Application: ..." certificate in the keychain) every binary is
# signed with the hardened runtime and a secure timestamp, inside out, with
# scripts/mac-entitlements.plist. With the App Store Connect API key as well
# (MAC_NOTARY_KEY_PATH to the .p8 file, MAC_NOTARY_KEY_ID, MAC_NOTARY_ISSUER)
# the zip goes to Apple's notary service and the ticket is stapled to the app,
# so it opens on a Mac that is offline too; then the zip is made again.
set -euo pipefail

APP="$1"
ZIP="$2"
ENTITLEMENTS="$(cd "$(dirname "$0")" && pwd)/mac-entitlements.plist"
EXECUTABLE="$APP/Contents/MacOS/StickyAIusage"

zip_app() {
  rm -f "$ZIP"
  (cd "$(dirname "$APP")" && ditto -c -k --keepParent "$(basename "$APP")" "$ZIP")
}

if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
  # Nested code first: the .NET runtime's libraries and helpers (any Mach-O file).
  find "$APP/Contents/MacOS" -type f ! -path "$EXECUTABLE" -print0 | while IFS= read -r -d '' file; do
    if file -b "$file" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime --sign "$MAC_SIGN_IDENTITY" "$file"
    fi
  done
  codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$MAC_SIGN_IDENTITY" "$EXECUTABLE"
  codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$MAC_SIGN_IDENTITY" "$APP"
  codesign --verify --strict --verbose=2 "$APP"
  echo "Signed with $MAC_SIGN_IDENTITY"
else
  codesign --force --deep --sign - "$APP"
fi
zip_app

if [ -n "${MAC_SIGN_IDENTITY:-}" ] && [ -n "${MAC_NOTARY_KEY_PATH:-}" ] && [ -n "${MAC_NOTARY_KEY_ID:-}" ] && [ -n "${MAC_NOTARY_ISSUER:-}" ]; then
  xcrun notarytool submit "$ZIP" --key "$MAC_NOTARY_KEY_PATH" --key-id "$MAC_NOTARY_KEY_ID" \
    --issuer "$MAC_NOTARY_ISSUER" --wait
  # Fails unless Apple accepted the submission: the release stops here instead
  # of publishing an app that isn't notarized.
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  zip_app
  echo "Notarized and stapled"
fi
