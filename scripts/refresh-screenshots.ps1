<#
.SYNOPSIS
  Redraws every README picture from the demo's sample data and copies them into docs/images.

.DESCRIPTION
  Runs the two screenshot test projects with UI_MARKETING_DIR set to a temporary folder:
  the macOS screens, the share cards, the social preview and docs/images/demo.gif come from the
  Avalonia headless tests (any OS), the Windows screens from the WPF tests (Windows, in an
  unlocked session: WPF draws nothing while the session is locked, and the tests then refuse
  to hand over blank pictures). Then copies the pictures over the ones in docs/images.
  Nothing is copied when a test fails. Plain ASCII on purpose (Windows PowerShell 5.1).

.PARAMETER SkipWindows
  Leave the Windows pictures as they are (a Mac, CI, or a locked Windows session).

.EXAMPLE
  pwsh scripts/refresh-screenshots.ps1
  pwsh scripts/refresh-screenshots.ps1 -SkipWindows
#>
param([switch]$SkipWindows)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$images = Join-Path $root 'docs/images'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('stickyaiusage-refresh-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$output = Join-Path $work 'pictures'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$env:UI_MARKETING_DIR = $output

function Invoke-Tests([string]$project, [string]$filter, [string[]]$extra) {
    Write-Host "Drawing with $project ..."
    & dotnet test (Join-Path $root $project) -c Release --filter $filter @extra | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$project failed (exit $LASTEXITCODE); no picture was copied." }
}

try {
    Invoke-Tests 'tests/StickyAIusage.Mac.UiTests' 'FullyQualifiedName~MarketingTests' @()
    $windows = -not $SkipWindows -and ($env:OS -eq 'Windows_NT')
    if ($windows) {
        # Its own build folder: an installed or running copy of the app may lock the usual one.
        Invoke-Tests 'tests/StickyAIusage.UiTests' 'FullyQualifiedName~Every_screen_renders' @("-p:OutDir=$(Join-Path $work 'build')/")
    }

    $folders = @('mac', 'share')
    if ($windows) { $folders += 'windows' }
    foreach ($folder in $folders) {
        $target = Join-Path $images $folder
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($picture in Get-ChildItem -Path (Join-Path $output $folder) -Filter '*.png') {
            Copy-Item -Path $picture.FullName -Destination $target -Force
            Write-Host "  docs/images/$folder/$($picture.Name)"
        }
    }
    foreach ($name in @('social-preview.png', 'demo.gif')) {
        Copy-Item -Path (Join-Path $output $name) -Destination $images -Force
        Write-Host "  docs/images/$name"
    }
    if (-not $windows) { Write-Host 'Windows pictures left as they are (-SkipWindows, or not on Windows).' }
    Write-Host 'Done. Review the pictures (git diff --stat docs/images) before committing.'
}
finally {
    Remove-Item Env:UI_MARKETING_DIR -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
}
