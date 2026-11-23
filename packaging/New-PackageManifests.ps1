<#
.SYNOPSIS
    Fills the package-manager manifests in this folder for one release.

.DESCRIPTION
    Every *.template file under packaging/ is copied to -OutDir (same relative path, without
    ".template"), with these placeholders replaced, in the file and in its path:

        {{VERSION}}            the release version without the "v" (2.0.0, 2.0.0-beta.2)
        {{SHA256:<file>}}      the SHA-256 of <file>, taken from the release's checksums.txt

    A placeholder left unfilled, a file missing from checksums.txt or a JSON file that doesn't
    parse fails the run (exit code 1), so a release never hands over a half-filled manifest.

        pwsh packaging/New-PackageManifests.ps1 -Version v2.0.0 -ChecksumsFile downloads/checksums.txt -OutDir manifests

    The Release workflow runs it and uploads the result as the "package-manifests" artifact;
    packaging/README.md says what to do with each file. Plain ASCII (Windows PowerShell 5.1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ChecksumsFile,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

function Read-Checksums {
    param([string]$Path)
    $sums = @{}
    foreach ($line in (Get-Content -LiteralPath $Path)) {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$') { $sums[$Matches[2]] = $Matches[1].ToLowerInvariant() }
    }
    return $sums
}

function Expand-Placeholders {
    param([string]$Text, [string]$Version, [hashtable]$Sums, [string]$Where)
    $text = $Text.Replace('{{VERSION}}', $Version)
    $missing = New-Object System.Collections.Generic.List[string]
    $text = [regex]::Replace($text, '\{\{SHA256:([^}]+)\}\}', {
        param($match)
        $name = $match.Groups[1].Value
        if ($Sums.ContainsKey($name)) { return $Sums[$name] }
        $missing.Add($name)
        return $match.Value
    })
    if ($missing.Count -gt 0) { throw "$Where needs the SHA-256 of $($missing -join ', '), which checksums.txt doesn't list." }
    if ($text -match '\{\{[^}]*\}\}') { throw "$Where has an unknown placeholder $($Matches[0])." }
    return $text
}

try {
    $version = $Version.TrimStart('v')
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "'$Version' isn't a release version such as v2.0.0 or v2.0.0-beta.2." }
    $sums = Read-Checksums -Path $ChecksumsFile
    if ($sums.Count -eq 0) { throw "$ChecksumsFile has no checksum lines." }

    $root = $PSScriptRoot
    $templates = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.template')
    if ($templates.Count -eq 0) { throw "No *.template files under $root." }
    foreach ($template in $templates) {
        $relative = $template.FullName.Substring($root.Length + 1)
        $relative = $relative.Substring(0, $relative.Length - '.template'.Length).Replace('{{VERSION}}', $version)
        $text = Expand-Placeholders -Text ([IO.File]::ReadAllText($template.FullName)) -Version $version -Sums $sums -Where $relative
        if ($relative.EndsWith('.json')) {
            try { [void]($text | ConvertFrom-Json) } catch { throw "$relative isn't valid JSON after filling it in: $($_.Exception.Message)" }
        }
        $target = Join-Path $OutDir $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        # UTF-8 without a byte-order mark, LF line ends: what both package repositories expect.
        [IO.File]::WriteAllText($target, $text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "  $relative"
    }
    exit 0
}
catch {
    Write-Host "New-PackageManifests: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
