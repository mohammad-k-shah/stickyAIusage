#!/bin/sh
# Installs, updates or removes Sticky AI Usage on macOS (no admin rights needed).
#
#   curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh
#
# With options (after "sh -s --"):
#
#   curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh -s -- --uninstall
#
#   --version TAG   a release tag such as v2.0.0 (default: the latest release)
#   --prerelease    the newest release, including pre-releases (betas)
#   --uninstall     remove the app and its "Launch at login" entry
#   --purge         with --uninstall: also delete the settings, cache and logs
#   --no-open       don't open the app after installing
#   --dir FOLDER    where the app goes (default: /Applications, or ~/Applications
#                   when /Applications isn't writable)
#
# It downloads the build for this Mac (Apple Silicon or Intel) from the
# project's GitHub Releases page, checks it against the release's
# checksums.txt (SHA-256) and refuses anything that doesn't match.
#
# Exit codes: 0 done, 1 download or file error, 2 the download didn't match
# checksums.txt, 3 a running copy wasn't quit.
set -eu

REPO="mohammad-k-shah/stickyAIusage"
APP_NAME="Sticky AI Usage.app"
EXE_NAME="StickyAIusage"

say() { printf '%s\n' "$*"; }
step() { printf '  %s\n' "$*"; }
fail() { printf 'Sticky AI Usage: %s\n' "$*" >&2; }

# The help text lives here, not in a read-back of this file: under "curl | sh"
# there is no file to read.
usage() {
    cat <<'EOF'
Installs, updates or removes Sticky AI Usage on macOS (no admin rights needed).

  curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh
  curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh -s -- OPTIONS

  --version TAG   a release tag such as v2.0.0 (default: the latest release)
  --prerelease    the newest release, including pre-releases (betas)
  --uninstall     remove the app and its "Launch at login" entry
  --purge         with --uninstall: also delete the settings, cache and logs
  --no-open       don't open the app after installing
  --dir FOLDER    where the app goes (default: /Applications, or ~/Applications
                  when /Applications isn't writable)

The download is checked against the release's checksums.txt (SHA-256); anything
that doesn't match is refused. Exit codes: 0 done, 1 download or file error,
2 the download didn't match checksums.txt, 3 a running copy wasn't quit.
EOF
}

# Everything runs from main at the bottom, so a download cut short runs nothing.

asset_for_this_mac() {
    if [ -n "${STICKY_INSTALL_ARCH:-}" ]; then
        arch="$STICKY_INSTALL_ARCH"
    elif [ "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" = "1" ]; then
        arch="arm64"   # also right in a Terminal running under Rosetta
    else
        arch="$(uname -m)"
    fi
    case "$arch" in
        arm64|aarch64) echo "StickyAIusage-mac-apple-silicon.zip" ;;
        *) echo "StickyAIusage-mac-intel.zip" ;;
    esac
}

# Sets RELEASE_BASE and RELEASE_LABEL, or fails.
resolve_release() {
    if [ -n "${STICKY_INSTALL_BASE_URL:-}" ]; then
        # Tests (and anyone mirroring the release) give a folder or URL holding
        # the release files directly.
        RELEASE_BASE="${STICKY_INSTALL_BASE_URL%/}"
        RELEASE_LABEL="from $RELEASE_BASE"
        return 0
    fi
    if [ -n "$VERSION" ]; then
        RELEASE_BASE="https://github.com/$REPO/releases/download/$VERSION"
        RELEASE_LABEL="$VERSION"
        return 0
    fi
    if [ "$PRERELEASE" = "1" ]; then
        api="https://api.github.com/repos/$REPO/releases?per_page=20"
    else
        api="https://api.github.com/repos/$REPO/releases/latest"
    fi
    if ! answer="$(curl -fsS -A sticky-ai-usage-installer "$api" 2>&1)"; then
        case "$answer" in
            *403*|*429*) fail "GitHub's limit for anonymous requests from your address is used up for now (60 an hour). Try again later, or pass --version with a release tag such as v2.0.0." ;;
            *404*) fail "No release found at $api." ;;
            *) fail "Couldn't reach GitHub ($answer). Check your internet connection or proxy." ;;
        esac
        return 1
    fi
    # The newest release comes first; no JSON tool needed for one field.
    tag="$(printf '%s\n' "$answer" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
    if [ -z "$tag" ]; then
        fail "No release found at $api."
        return 1
    fi
    RELEASE_BASE="https://github.com/$REPO/releases/download/$tag"
    RELEASE_LABEL="$tag"
}

# fetch NAME DESTINATION: 0 fetched, 4 not in the release, 1 failed.
fetch() {
    case "$RELEASE_BASE" in
        http://*|https://*)
            code="$(curl -sSL -A sticky-ai-usage-installer -o "$2" -w '%{http_code}' "$RELEASE_BASE/$1" 2>/dev/null || true)"
            case "$code" in
                200) return 0 ;;
                404) return 4 ;;
                *) fail "Couldn't download $RELEASE_BASE/$1 (HTTP ${code:-error}). Check your internet connection or proxy."; return 1 ;;
            esac ;;
        *)
            [ -f "$RELEASE_BASE/$1" ] || return 4
            cp "$RELEASE_BASE/$1" "$2" ;;
    esac
}

sha256_of() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | cut -d ' ' -f 1
    else
        sha256sum "$1" | cut -d ' ' -f 1
    fi
}

# checksums.txt is in the sha256sum format: "<64 hex>  <file name>".
expected_hash() {
    awk -v name="$2" '($2 == name || $2 == "*" name) && $1 ~ /^[0-9A-Fa-f]+$/ && length($1) == 64 { print tolower($1); exit }' "$1"
}

running_copy() {
    command -v pgrep >/dev/null 2>&1 || return 1
    pgrep -f "$1/$APP_NAME/Contents/MacOS/$EXE_NAME" >/dev/null 2>&1
}

# Never quits the app itself: the person quits it from the menu bar, and
# nothing is lost. "curl | sh" reads the script from standard input, so the
# answer comes from the terminal.
wait_for_quit() {
    while running_copy "$1"; do
        if [ "${STICKY_INSTALL_NONINTERACTIVE:-}" = "1" ] || ! { : </dev/tty; } 2>/dev/null; then
            fail "Sticky AI Usage is running from $1. Quit it first (click it in the menu bar > Quit), then run this again."
            return 1
        fi
        say "Sticky AI Usage is running. Click it in the menu bar, choose Quit, then press Return here (Ctrl+C cancels)."
        read -r _ </dev/tty || true
    done
}

default_dir() {
    if [ -n "${STICKY_INSTALL_DIR:-}" ]; then
        echo "$STICKY_INSTALL_DIR"
    elif [ -w /Applications ]; then
        echo "/Applications"
    else
        echo "$HOME/Applications"
    fi
}

install_app() {
    case "$(uname -s)" in
        Darwin) ;;
        *)
            if [ -z "${STICKY_INSTALL_BASE_URL:-}" ]; then
                fail "This script is for macOS. On Windows run scripts/install.ps1 (see the README)."
                return 1
            fi ;;
    esac

    asset="$(asset_for_this_mac)"
    resolve_release || return 1
    say "Installing Sticky AI Usage $RELEASE_LABEL"

    work="$(mktemp -d "${TMPDIR:-/tmp}/sticky-install.XXXXXX")"
    trap 'rm -rf "$work"' EXIT

    status=0
    fetch checksums.txt "$work/checksums.txt" || status=$?
    if [ "$status" = "4" ]; then
        if [ -n "$VERSION" ]; then
            fail "There is no release $VERSION, or it has no checksums.txt (releases before v2.0.0 don't). Nothing was installed."
        elif [ "$PRERELEASE" = "1" ] || [ -n "${STICKY_INSTALL_BASE_URL:-}" ]; then
            fail "Release $RELEASE_LABEL has no checksums.txt, so the download can't be checked (releases before v2.0.0 don't have one). Nothing was installed."
        else
            fail "Release $RELEASE_LABEL has no checksums.txt, so the download can't be checked (releases before v2.0.0 don't have one). Nothing was installed. For the newest beta run: curl -fsSL https://raw.githubusercontent.com/$REPO/main/scripts/install.sh | sh -s -- --prerelease"
        fi
        return 2
    elif [ "$status" != "0" ]; then
        return 1
    fi
    status=0
    fetch "$asset" "$work/$asset" || status=$?
    if [ "$status" = "4" ]; then
        fail "The release has no $asset. Nothing was installed."
        return 1
    elif [ "$status" != "0" ]; then
        return 1
    fi

    expected="$(expected_hash "$work/checksums.txt" "$asset")"
    if [ -z "$expected" ]; then
        fail "checksums.txt has no line for $asset, so the download can't be checked. Nothing was installed."
        return 2
    fi
    actual="$(sha256_of "$work/$asset" | tr 'A-F' 'a-f')"
    if [ "$actual" != "$expected" ]; then
        fail "The download doesn't match checksums.txt (expected $expected, got $actual). It may have been changed on the way, for example by a proxy. Nothing was installed."
        return 2
    fi
    step "Checked $asset (SHA-256 $actual)"

    mkdir -p "$work/files"
    if command -v ditto >/dev/null 2>&1; then
        unpacked=0; ditto -x -k "$work/$asset" "$work/files" || unpacked=$?
    else
        unpacked=0; unzip -q "$work/$asset" -d "$work/files" || unpacked=$?
    fi
    if [ "$unpacked" != "0" ]; then
        fail "Couldn't unpack $asset. Nothing was installed."
        return 1
    fi
    if [ ! -d "$work/files/$APP_NAME" ]; then
        fail "$asset has no $APP_NAME in it. Nothing was installed."
        return 1
    fi

    wait_for_quit "$DIR" || return 3
    mkdir -p "$DIR"
    rm -rf "${DIR:?}/$APP_NAME"
    mv "$work/files/$APP_NAME" "$DIR/$APP_NAME"
    step "Installed in $DIR/$APP_NAME"

    if [ "$OPEN" = "1" ] && command -v open >/dev/null 2>&1; then
        open "$DIR/$APP_NAME"
        say "Sticky AI Usage is in your menu bar, left of your status icons."
        say "If macOS says it can't check the app: System Settings > Privacy & Security > Open Anyway (the download isn't notarized yet)."
    fi
    say "To remove it later: curl -fsSL https://raw.githubusercontent.com/$REPO/main/scripts/install.sh | sh -s -- --uninstall"
}

uninstall_app() {
    wait_for_quit "$DIR" || return 3

    # "Launch at login" entries of this copy (one per profile:
    # com.stickyaiusage.widget[-<profile>]); an entry for a copy somewhere else
    # is left alone.
    # The plist holds the path XML-escaped (an apostrophe in a folder name is &apos;).
    escaped="$(printf '%s' "$DIR/$APP_NAME/" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g")"
    for plist in "$HOME"/Library/LaunchAgents/com.stickyaiusage.widget*.plist; do
        [ -f "$plist" ] || continue
        if grep -F -q -e "$DIR/$APP_NAME/" -e "$escaped" "$plist"; then
            if command -v launchctl >/dev/null 2>&1; then
                launchctl bootout "gui/$(id -u)" "$plist" >/dev/null 2>&1 || true
            fi
            rm -f "$plist"
            step "Removed the Launch at login entry $(basename "$plist")"
        fi
    done

    if [ -d "$DIR/$APP_NAME" ]; then
        rm -rf "${DIR:?}/$APP_NAME"
        step "Removed $DIR/$APP_NAME"
    else
        say "No Sticky AI Usage in $DIR."
    fi

    data="$HOME/Library/Application Support/StickyAIusage"
    logs="$HOME/Library/Logs/StickyAIusage"
    if [ "$PURGE" = "1" ]; then
        rm -rf "$data" "$logs"
        step "Deleted the settings, cache and logs"
    elif [ -d "$data" ]; then
        say "Your settings stay in $data (--purge deletes them)."
    fi
}

main() {
    VERSION=""
    PRERELEASE=0
    UNINSTALL=0
    PURGE=0
    OPEN=1
    DIR=""
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --version) [ "$#" -ge 2 ] || { fail "--version needs a tag, such as v2.0.0."; return 1; }; VERSION="$2"; shift ;;
            --prerelease) PRERELEASE=1 ;;
            --uninstall) UNINSTALL=1 ;;
            --purge) PURGE=1 ;;
            --no-open) OPEN=0 ;;
            --dir) [ "$#" -ge 2 ] || { fail "--dir needs a folder."; return 1; }; DIR="$2"; shift ;;
            -h|--help) usage; return 0 ;;
            *) fail "Unknown option $1 (see --help)."; return 1 ;;
        esac
        shift
    done
    if [ "$PURGE" = "1" ] && [ "$UNINSTALL" != "1" ]; then
        fail "--purge goes with --uninstall (it deletes the settings when the app is removed). Nothing was changed."
        return 1
    fi
    [ -n "$DIR" ] || DIR="$(default_dir)"
    DIR="${DIR%/}"

    if [ "$UNINSTALL" = "1" ]; then
        uninstall_app
    else
        install_app
    fi
}

main "$@"
