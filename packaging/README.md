# Packaging and install channels

How Sticky AI Usage reaches people, and how each channel is updated. Every channel uses the files the **Release**
workflow (`.github/workflows/release.yml`) publishes for a tag:

| Release file | What | Used by |
|---|---|---|
| `StickyAIusage-win-x64.zip` | `StickyAIusage.exe` alone: self-contained single file, needs no .NET install (about 66 MB) | `scripts/install.ps1`, Scoop, manual download |
| `StickyAIusage-win-x64-small.zip` | the same exe, framework-dependent: needs the .NET 10 Desktop Runtime (well under 1 MB) | manual download |
| `StickyAIusage-mac-apple-silicon.zip`, `StickyAIusage-mac-intel.zip` | `Sticky AI Usage.app`, ad-hoc signed | `scripts/install.sh`, manual download |
| `checksums.txt` | SHA-256 of every zip, in `sha256sum`'s format | both scripts (they refuse a download that differs), people checking by hand |

A tag with a hyphen (`v2.0.0-beta.1`) is published as a **pre-release**. GitHub's "latest release", which the
scripts install by default, never points at a pre-release; `-Prerelease` / `--prerelease` (or `-Version` /
`--version`) installs one. Releases before v2.0.0 have no `checksums.txt`, and the scripts refuse them.

## Install scripts (work from the first release that has `checksums.txt`)

- Windows: `irm https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.ps1 | iex`
- macOS: `curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh`

Both are served from `main`, so a change reaches people when it is merged there. Exit codes, the same in both:
0 done, 1 download or file error, 2 the download didn't match `checksums.txt` (or the release has none), 3 a running
copy wasn't closed (the scripts never close it: they ask), 4 (Windows) the folder belongs to the Setup/winget install.

What they touch, and nothing else:

| | Windows (`install.ps1`) | macOS (`install.sh`) |
|---|---|---|
| App | `%LOCALAPPDATA%\Programs\Sticky AI Usage\` (only the files listed in its `installed-files.txt`) | `/Applications/Sticky AI Usage.app` (or `~/Applications`) |
| Shortcut | Start Menu `Sticky AI Usage.lnk` | — |
| Uninstall also removes | "Start with Windows" values `StickyAIusage[-<profile>]` that point at this copy | LaunchAgents `com.stickyaiusage.widget[-<profile>].plist` that point at this copy |
| `-Purge` / `--purge` also removes | `%LOCALAPPDATA%\StickyAIusage` | `~/Library/Application Support/StickyAIusage`, `~/Library/Logs/StickyAIusage` |

Test hooks (used by `tests/StickyAIusage.Tests/InstallScriptTests.cs`; harmless for anyone else):
`STICKY_INSTALL_BASE_URL` (a folder or URL holding the release files directly), `STICKY_INSTALL_NONINTERACTIVE=1`
(never wait for an answer), `STICKY_INSTALL_RUN_KEY` (Windows: the registry key used instead of `HKCU\…\Run`),
`STICKY_INSTALL_ARCH` and `STICKY_INSTALL_DIR` (macOS). The Windows tests also point `LOCALAPPDATA` and `APPDATA` at
a temp folder, and the macOS tests `HOME`.

## Package-manager manifests

`New-PackageManifests.ps1` fills every `*.template` file here with a release's version and the hashes from its
`checksums.txt`. The Release workflow runs it and uploads the result as the **package-manifests** artifact of the
run. Nothing is submitted anywhere from the workflow.

```powershell
pwsh packaging/New-PackageManifests.ps1 -Version v2.0.0 -ChecksumsFile checksums.txt -OutDir manifests
```

### Scoop (`scoop/sticky-ai-usage.json.template`)

Not published yet. To start it (the maintainer, once, from a stable release that has `checksums.txt`):

1. Create the public repository `mohammad-k-shah/scoop-bucket` from Scoop's
   [bucket template](https://github.com/ScoopInstaller/BucketTemplate) (it brings the workflow that keeps manifests
   up to date).
2. Copy `scoop/sticky-ai-usage.json` from that release's **package-manifests** artifact to `bucket/sticky-ai-usage.json`
   there and commit it.
3. People then run `scoop bucket add sticky-ai-usage https://github.com/mohammad-k-shah/scoop-bucket` and
   `scoop install sticky-ai-usage`.

After that the bucket updates itself: `checkver: github` sees each new stable release (Scoop ignores pre-releases)
and `autoupdate` takes the new hash from that release's `checksums.txt`. The main repository needs no token for it.

### winget

Package `MohammadKShah.StickyAIUsage`, installer type `inno`, scope `user`, from the release's
`StickyAIusage-Setup-x64.exe` (`installer/windows/setup.iss`: per-user, no admin, `%LOCALAPPDATA%\Programs\Sticky AI
Usage`, Start Menu shortcut; Setup and Uninstall stop while the widget runs and ask to exit it; uninstall removes only
this copy's start entries and asks before deleting the settings, which a silent uninstall keeps). The templates in
`winget/manifests/…/{{VERSION}}/` mirror the `microsoft/winget-pkgs` layout; the `ProductCode` is the installer's
`AppId` + `_is1` (a test keeps them equal). The installer manifest passes `/CHANNEL=winget` (`InstallerSwitches: Custom`),
so the Setup writes `install-method` = "winget" and the app's update notice says `winget upgrade MohammadKShah.StickyAIUsage`;
keep that switch when a manifest is made another way (`wingetcreate new` doesn't add it).

To try the installer on a computer where the widget is running, compile a test build that watches a developer
profile's lock and a scratch settings folder, install it into a scratch folder, and run the copy with that profile:

```powershell
iscc /DAppVersion=2.0.0-test /DSourceDir=<publish folder> /DAppMutexName=StickyAIusage.SingleInstance.dev `
     /DDataDir=<scratch folder> /O<output folder> installer\windows\setup.iss
<output folder>\StickyAIusage-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=<scratch install folder>
```

The Apps entry and the Start Menu shortcut are still the real ones; uninstall the test copy to remove them.

1. **First submission (the maintainer, by hand, at least a week before it is announced):** take
   `winget/manifests/…` from a release's **package-manifests** artifact, run `winget validate --manifest <folder>`,
   and open a pull request with that folder in a fork of `microsoft/winget-pkgs` (or run
   `wingetcreate new <Setup URL>`). Review takes days.
2. **After it is merged:** set the repository variable `WINGET_AUTO_SUBMIT` to `true` and the secret `WINGET_TOKEN`
   (a classic token with `public_repo`). The Release workflow's `winget` job then runs `wingetcreate update … --submit`
   for each stable tag (never for a pre-release).

Until the package is listed, the README points Windows users at the script.

### Homebrew

Only after the Mac app is notarized: Homebrew quarantines cask downloads, so an un-notarized cask would be blocked on
its first launch. Until then the Mac line is the script.
