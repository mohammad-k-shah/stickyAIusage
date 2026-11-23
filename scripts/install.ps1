<#
.SYNOPSIS
    Installs, updates or removes Sticky AI Usage for the current Windows user (no admin rights needed).

.DESCRIPTION
    Downloads the self-contained Windows build from the project's GitHub Releases page, checks it
    against the release's checksums.txt (SHA-256) and refuses anything that doesn't match, unpacks it
    to %LOCALAPPDATA%\Programs\Sticky AI Usage, adds a Start Menu shortcut and starts the widget.
    Running it again updates the widget in place. Nothing else on the computer is changed.

        irm https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.ps1 | iex

    With options:

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.ps1))) -Uninstall

    Exit codes: 0 done, 1 download or file error, 2 the download didn't match checksums.txt,
    3 a running copy wasn't closed, 4 the folder belongs to the Setup (winget) install.

.PARAMETER Version
    A release tag such as v2.0.0. Default: the latest release.

.PARAMETER Prerelease
    Take the newest release, including pre-releases (betas).

.PARAMETER Uninstall
    Removes the widget, its Start Menu shortcut and its "Start with Windows" entry.

.PARAMETER Purge
    With -Uninstall: also deletes the settings, cache and logs (%LOCALAPPDATA%\StickyAIusage). Folders of
    developer profiles (StickyAIusage-<name>) stay.

.PARAMETER NoStart
    Doesn't start the widget after installing.

.PARAMETER InstallDir
    Where the widget goes. Default: %LOCALAPPDATA%\Programs\Sticky AI Usage.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Prerelease,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$NoStart,
    [string]$InstallDir
)

# Everything runs inside functions, so a download cut short runs nothing, and
# "irm | iex" leaves no settings behind in the caller's PowerShell session.
# This file is plain ASCII on purpose: Windows PowerShell 5.1 reads a file
# without a byte-order mark in the system code page.

function Invoke-StickyInstall {
    param([hashtable]$Options)

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar

    $repository = 'mohammad-k-shah/stickyAIusage'
    $displayName = 'Sticky AI Usage'
    $exeName = 'StickyAIusage.exe'
    $assetName = 'StickyAIusage-win-x64.zip'
    $markerName = 'installed-files.txt'

    if ($Options.Purge -and -not $Options.Uninstall) {
        Write-Failure "-Purge goes with -Uninstall (it deletes the settings when the app is removed). Nothing was changed."
        return 1
    }
    $installDir = $Options.InstallDir
    if (-not $installDir) { $installDir = Join-Path $env:LOCALAPPDATA "Programs\$displayName" }
    # Relative to PowerShell's current folder, not the process's.
    $installDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($installDir).TrimEnd('\')
    $shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$displayName.lnk"
    # Tests point this at a key of their own, so they never touch the real Run key.
    $runKey = $env:STICKY_INSTALL_RUN_KEY
    if (-not $runKey) { $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' }

    if ($Options.Uninstall) {
        return Uninstall-Sticky -InstallDir $installDir -ShortcutPath $shortcutPath -RunKey $runKey `
            -MarkerName $markerName -ExeName $exeName -Purge:$Options.Purge
    }

    if (Test-Path (Join-Path $installDir 'unins000.exe')) {
        Write-Failure "$displayName in $installDir was installed with its Setup (or winget). Update it with 'winget upgrade MohammadKShah.StickyAIUsage' or the newest Setup from the Releases page, or remove it in Settings > Apps first."
        return 4
    }

    # 1. Where the files come from.
    $source = Resolve-ReleaseSource -Repository $repository -Version $Options.Version -Prerelease:$Options.Prerelease
    if ($source -is [int]) { return $source }
    Write-Step "Installing $displayName $($source.Label)"

    $work = Join-Path ([IO.Path]::GetTempPath()) ("sticky-install-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        # 2. Download and check.
        $zip = Join-Path $work $assetName
        $sums = Join-Path $work 'checksums.txt'
        if (-not (Get-ReleaseFile -Source $source -Name 'checksums.txt' -Destination $sums)) {
            $text = "Release $($source.Label) has no checksums.txt, so the download can't be checked (releases before v2.0.0 don't have one). Nothing was installed."
            if ($Options.Version) { $text = "There is no release $($Options.Version), or it has no checksums.txt (releases before v2.0.0 don't). Nothing was installed." }
            elseif (-not $Options.Prerelease -and -not $env:STICKY_INSTALL_BASE_URL) {
                $text += " For the newest beta run: & ([scriptblock]::Create((irm https://raw.githubusercontent.com/$repository/main/scripts/install.ps1))) -Prerelease"
            }
            Write-Failure $text
            return 2
        }
        if (-not (Get-ReleaseFile -Source $source -Name $assetName -Destination $zip)) {
            Write-Failure "The release has no $assetName. Nothing was installed."
            return 1
        }
        $expected = Get-ExpectedHash -ChecksumsFile $sums -Name $assetName
        if (-not $expected) {
            Write-Failure "checksums.txt has no line for $assetName, so the download can't be checked. Nothing was installed."
            return 2
        }
        $actual = Get-Sha256 -Path $zip
        if ($actual -ne $expected) {
            Write-Failure "The download doesn't match checksums.txt (expected $expected, got $actual). It may have been changed on the way, for example by a proxy. Nothing was installed."
            return 2
        }
        Write-Step "Checked $assetName (SHA-256 $($actual.ToLowerInvariant()))"

        $staging = Join-Path $work 'files'
        Expand-Zip -Path $zip -Destination $staging
        if (-not (Test-Path (Join-Path $staging $exeName))) {
            Write-Failure "$assetName has no $exeName in it. Nothing was installed."
            return 1
        }

        # 3. An older copy running from this folder holds its files.
        if (-not (Wait-ForExit -InstallDir $installDir -ExeName $exeName)) { return 3 }

        # 4. Replace the files: only the ones this script put there, so a folder
        #    given with -InstallDir never loses anything else.
        New-Item -ItemType Directory -Force -Path $installDir | Out-Null
        $newFiles = @(Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
            $_.FullName.Substring($staging.Length + 1)
        })
        # How this copy was installed: the app's update notice then names this script.
        $newFiles += 'install-method'
        $markerPath = Join-Path $installDir $markerName
        if (Test-Path $markerPath) {
            foreach ($old in (Get-Content -LiteralPath $markerPath)) {
                if ($old -and ($newFiles -notcontains $old)) {
                    Remove-Item -LiteralPath (Join-Path $installDir $old) -Force -ErrorAction SilentlyContinue
                }
            }
        }
        # Piped items bind to -LiteralPath: a temp path with [ or ] in it is no wildcard pattern.
        Get-ChildItem -LiteralPath $staging | Copy-Item -Destination $installDir -Recurse -Force
        Set-Content -LiteralPath (Join-Path $installDir 'install-method') -Value 'script' -Encoding ASCII
        Set-Content -LiteralPath $markerPath -Value $newFiles -Encoding ASCII

        # 5. Start Menu shortcut.
        $exePath = Join-Path $installDir $exeName
        New-Shortcut -Path $shortcutPath -Target $exePath -Description 'Your Claude usage limits, always in sight'

        Write-Step "Installed in $installDir"
        if (-not $Options.NoStart) {
            Start-Process -FilePath $exePath -WorkingDirectory $installDir
            Write-Host "$displayName is running: look in your taskbar, left of the tray icons (or just above them when the taskbar is full). Start Menu: $displayName."
        }
        Write-Host "To remove it later: run this script again with -Uninstall."
        return 0
    }
    catch {
        Write-Failure "Install failed: $($_.Exception.Message)"
        return 1
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-ReleaseSource {
    param([string]$Repository, [string]$Version, [switch]$Prerelease)

    # Tests (and anyone mirroring the release) give a folder or URL holding the
    # release files directly.
    $base = $env:STICKY_INSTALL_BASE_URL
    if ($base) {
        return @{ Base = $base.TrimEnd('/', '\'); Label = "from $base" }
    }
    if ($Version) {
        return @{ Base = "https://github.com/$Repository/releases/download/$Version"; Label = $Version }
    }

    $api = "https://api.github.com/repos/$Repository/releases"
    if (-not $Prerelease) { $api = "$api/latest" } else { $api = "$api`?per_page=20" }
    try {
        $answer = Invoke-RestMethod -UseBasicParsing -Uri $api -Headers @{ 'User-Agent' = 'sticky-ai-usage-installer' }
    }
    catch {
        $status = Get-HttpStatus $_
        if ($status -eq 403 -or $status -eq 429) {
            Write-Failure "GitHub's limit for anonymous requests from your address is used up for now (60 an hour). Try again later, or pass -Version with a release tag such as v2.0.0."
        }
        elseif ($status -eq 404) {
            Write-Failure "No release found at $api."
        }
        else {
            Write-Failure "Couldn't reach GitHub ($($_.Exception.Message)). Check your internet connection or proxy."
        }
        return 1
    }
    # Windows PowerShell hands a JSON array over as one object: no pipeline here.
    $release = $null
    foreach ($candidate in @($answer)) {
        if (-not $candidate.draft) { $release = $candidate; break }
    }
    if (-not $release -or -not $release.tag_name) {
        Write-Failure "No release found at $api."
        return 1
    }
    return @{ Base = "https://github.com/$Repository/releases/download/$($release.tag_name)"; Label = $release.tag_name }
}

function Get-ReleaseFile {
    param([hashtable]$Source, [string]$Name, [string]$Destination)

    $from = "$($Source.Base)/$Name"
    if ($from -notmatch '^https?://') {
        $local = Join-Path $Source.Base $Name
        if (-not (Test-Path -LiteralPath $local)) { return $false }
        Copy-Item -LiteralPath $local -Destination $Destination
        return $true
    }
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $from -OutFile $Destination -Headers @{ 'User-Agent' = 'sticky-ai-usage-installer' }
        return $true
    }
    catch {
        if ((Get-HttpStatus $_) -eq 404) { return $false }
        throw "couldn't download $from ($($_.Exception.Message))"
    }
}

function Get-HttpStatus {
    param($ErrorRecord)
    $response = $ErrorRecord.Exception.Response
    if ($null -eq $response) { return 0 }
    try { return [int]$response.StatusCode } catch { return 0 }
}

# Get-FileHash and Expand-Archive live in script modules that Windows PowerShell
# can't load when it inherits PowerShell 7's module path (started from pwsh, as
# on GitHub's runners); .NET does the same work without any module.
function Get-Sha256 {
    param([string]$Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Expand-Zip {
    param([string]$Path, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Path, $Destination)
}

function Get-ExpectedHash {
    # checksums.txt is in the sha256sum format: "<64 hex>  <file name>", one file a line.
    param([string]$ChecksumsFile, [string]$Name)
    foreach ($line in (Get-Content -LiteralPath $ChecksumsFile)) {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $Name) {
            return $Matches[1].ToUpperInvariant()
        }
    }
    return $null
}

function Get-RunningCopies {
    param([string]$InstallDir, [string]$ExeName)
    $prefix = $InstallDir + '\'
    @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

function Wait-ForExit {
    # Never closes the widget itself: it is a tray app without a main window,
    # so the person exits it from the tray, and nothing is lost.
    param([string]$InstallDir, [string]$ExeName)
    while ($true) {
        $running = Get-RunningCopies -InstallDir $InstallDir -ExeName $ExeName
        if ($running.Count -eq 0) { return $true }
        if (-not (Test-Interactive)) {
            Write-Failure "Sticky AI Usage is running from $InstallDir. Exit it first (right-click its tray icon > Exit), then run this again."
            return $false
        }
        Write-Host "Sticky AI Usage is running. Right-click its tray icon, choose Exit, then press Enter here (Ctrl+C cancels)."
        [void](Read-Host)
    }
}

function Test-Interactive {
    if ($env:STICKY_INSTALL_NONINTERACTIVE -eq '1') { return $false }
    if (-not [Environment]::UserInteractive) { return $false }
    foreach ($argument in [Environment]::GetCommandLineArgs()) {
        if ($argument -like '-NonI*') { return $false }
    }
    return $true
}

function New-Shortcut {
    param([string]$Path, [string]$Target, [string]$Description)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($Path)
        $link.TargetPath = $Target
        $link.WorkingDirectory = Split-Path -Parent $Target
        $link.Description = $Description
        $link.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

function Get-ShortcutTarget {
    param([string]$Path)
    $shell = New-Object -ComObject WScript.Shell
    try { return $shell.CreateShortcut($Path).TargetPath }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
}

function Uninstall-Sticky {
    param([string]$InstallDir, [string]$ShortcutPath, [string]$RunKey, [string]$MarkerName, [string]$ExeName, [switch]$Purge)

    try {
        $markerPath = Join-Path $InstallDir $MarkerName
        if (Test-Path (Join-Path $InstallDir 'unins000.exe')) {
            Write-Failure "Sticky AI Usage in $InstallDir was installed with its Setup (or winget). Remove it with 'winget uninstall MohammadKShah.StickyAIUsage' or in Settings > Apps."
            return 4
        }
        if (-not (Wait-ForExit -InstallDir $InstallDir -ExeName $ExeName)) { return 3 }

        $exePath = Join-Path $InstallDir $ExeName
        # "Start with Windows" entries of this copy (one per profile:
        # StickyAIusage, StickyAIusage-<profile>). An entry of a copy somewhere
        # else is left alone.
        if (Test-Path $RunKey) {
            $values = Get-ItemProperty -LiteralPath $RunKey
            foreach ($property in $values.PSObject.Properties) {
                if ($property.Name -match '^StickyAIusage(-[A-Za-z0-9_-]+)?$' -and
                    ([string]$property.Value).IndexOf($exePath, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    Remove-ItemProperty -LiteralPath $RunKey -Name $property.Name
                    Write-Step "Removed the Start with Windows entry $($property.Name)"
                }
            }
        }

        if ((Test-Path -LiteralPath $ShortcutPath) -and
            ((Get-ShortcutTarget -Path $ShortcutPath) -eq $exePath)) {
            Remove-Item -LiteralPath $ShortcutPath -Force
            Write-Step "Removed the Start Menu shortcut"
        }

        if (Test-Path -LiteralPath $markerPath) {
            foreach ($file in (Get-Content -LiteralPath $markerPath)) {
                if ($file) { Remove-Item -LiteralPath (Join-Path $InstallDir $file) -Force -ErrorAction SilentlyContinue }
            }
            Remove-Item -LiteralPath $markerPath -Force
            if (-not (Get-ChildItem -LiteralPath $InstallDir -Force)) { Remove-Item -LiteralPath $InstallDir -Force }
            Write-Step "Removed $InstallDir"
        }
        else {
            Write-Host "No copy installed by this script in $InstallDir."
        }

        if ($Purge) {
            $data = Join-Path $env:LOCALAPPDATA 'StickyAIusage'
            if (Test-Path -LiteralPath $data) {
                Remove-Item -LiteralPath $data -Recurse -Force
                Write-Step "Deleted the settings, cache and logs in $data"
            }
        }
        elseif (Test-Path (Join-Path $env:LOCALAPPDATA 'StickyAIusage')) {
            Write-Host "Your settings stay in $(Join-Path $env:LOCALAPPDATA 'StickyAIusage') (-Purge deletes them)."
        }
        return 0
    }
    catch {
        Write-Failure "Uninstall failed: $($_.Exception.Message)"
        return 1
    }
}

function Write-Step { param([string]$Text) Write-Host "  $Text" }
function Write-Failure { param([string]$Text) Write-Host "Sticky AI Usage: $Text" -ForegroundColor Red }

$stickyExitCode = Invoke-StickyInstall @{
    Version = $Version; Prerelease = [bool]$Prerelease; Uninstall = [bool]$Uninstall; Purge = [bool]$Purge
    NoStart = [bool]$NoStart; InstallDir = $InstallDir
}
if ($PSCommandPath) { exit $stickyExitCode }   # run as a file: the exit code is the result
$global:LASTEXITCODE = $stickyExitCode          # "irm | iex": exit would close the window
