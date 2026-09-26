# release-manifest.ps1 — Task 11 step 3.
#
# Walks a publish output directory and emits a deterministic JSON
# release manifest consumed by:
#   * the C# ReleaseManifestVerifier (asserts size/SHA-256/PE-arch)
#   * the WiX installer build (heat harvest)
#   * the e2e acceptance test (Task 12)
#
# Output schema mirrors the C# `ReleaseManifest` record exactly so
# the C# JsonSerializerContext can deserialise it without any
# additional mapping layer.
#
# Usage:
#   pwsh -File build/release-manifest.ps1 `
#        -PublishRoot publish/win-x64 `
#        -Output publish/win-x64/manifests/release.json

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishRoot,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$ProductVersion = '1.0.0',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$GitRevision = '',
    [string]$Configuration = 'Release',
    [bool]$Signed = $false,
    [string]$ModelManifestPath = 'manifests/model.json',
    [string]$BrowserManifestPath = 'browsers/playwright/chromium/browser-manifest.json',
    [string]$LicenseManifestPath = 'licenses/THIRD-PARTY-NOTICES.txt'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishRoot -PathType Container)) {
    throw "PublishRoot '$PublishRoot' does not exist or is not a directory."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$packageProps = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw
$webViewPackageVersion = $packageProps.SelectSingleNode("//PackageVersion[@Include='Avalonia.Controls.WebView']").Version
$playwrightPackageVersion = $packageProps.SelectSingleNode("//PackageVersion[@Include='Microsoft.Playwright']").Version
$runtimeSources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'windows/runtime-sources.json') -Raw | ConvertFrom-Json
$fixedRuntimeVersion = [string]$runtimeSources.webview2.version
$chromiumRevision = ''
foreach ($entry in $runtimeSources.playwright.expected_entries) {
    if ([string]$entry -match '^chromium-(\d+)$') {
        $chromiumRevision = $Matches[1]
        break
    }
}
if ([string]::IsNullOrWhiteSpace($webViewPackageVersion) -or
    [string]::IsNullOrWhiteSpace($playwrightPackageVersion) -or
    [string]::IsNullOrWhiteSpace($fixedRuntimeVersion) -or
    [string]::IsNullOrWhiteSpace($chromiumRevision)) {
    throw 'Release manifest runtime metadata is incomplete.'
}
if ([string]::IsNullOrWhiteSpace($GitRevision)) {
    try {
        $GitRevision = (& git -C $repoRoot rev-parse HEAD 2>$null | Select-Object -First 1).Trim()
    }
    catch {
        $GitRevision = ''
    }
    if ([string]::IsNullOrWhiteSpace($GitRevision)) { $GitRevision = 'snapshot' }
}

# Exclude generated manifest files so the manifest never hashes
# itself (which would invalidate the fingerprint).
$files = Get-ChildItem -LiteralPath $PublishRoot -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/]manifests[\\/]' } |
    Sort-Object -Property @{ Expression = { $_.FullName.Substring($PublishRoot.Length).TrimStart('\','/') } }

$assets = foreach ($file in @($files)) {
    $rel = $file.FullName.Substring($PublishRoot.Length).TrimStart('\','/').Replace('\','/')
    [pscustomobject]@{
        relativePath = $rel
        length       = $file.Length
        sha256       = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

# Hash the complete manifest payload without the self-referential digest field.
$manifest = [ordered]@{
    schemaVersion       = 1
    applicationVersion  = $ProductVersion
    gitRevision         = $GitRevision
    runtimeIdentifier   = $RuntimeIdentifier
    configuration       = $Configuration
    signed              = $Signed
    webView2             = [ordered]@{
        packageVersion      = $webViewPackageVersion
        fixedRuntimeVersion = $fixedRuntimeVersion
    }
    playwright          = [ordered]@{
        packageVersion   = $playwrightPackageVersion
        chromiumRevision = $chromiumRevision
    }
    assets              = @($assets | Sort-Object relativePath)
    modelManifestPath   = $ModelManifestPath
    browserManifestPath = $BrowserManifestPath
    licenseManifestPath = $LicenseManifestPath
}
$jsonForHash = $manifest | ConvertTo-Json -Depth 5 -Compress
$manifestSha256 = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($jsonForHash)
    )
).Replace('-', '').ToLowerInvariant()
$manifest.manifestSha256 = $manifestSha256

$dir = Split-Path -Parent $Output
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$json = ($manifest | ConvertTo-Json -Depth 5) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($Output, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Wrote release manifest: $Output"
Write-Host "  version: $ProductVersion"
Write-Host "  RID: $RuntimeIdentifier"
Write-Host "  assets: $(@($assets).Count)"
Write-Host "  manifest SHA-256: $manifestSha256"