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
    [string]$ProductName = 'InvoiceFlowAI',
    [string]$ProductVersion = '1.0.0',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishRoot -PathType Container)) {
    throw "PublishRoot '$PublishRoot' does not exist or is not a directory."
}

# Exclude generated manifest files so the manifest never hashes
# itself (which would invalidate the fingerprint).
$files = Get-ChildItem -LiteralPath $PublishRoot -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/]manifests[\\/]' } |
    Sort-Object -Property @{ Expression = { $_.FullName.Substring($PublishRoot.Length).TrimStart('\','/') } }

function Get-AssetKind {
    param([string]$RelativePath)
    $ext = [IO.Path]::GetExtension($RelativePath).ToLowerInvariant()
    switch ($ext) {
        '.exe' { return 'exe' }
        '.dll' { return 'native' }
        '.pdb' { return 'symbol' }
        '.json' { return 'config' }
        '.xml'  { return 'config' }
        '.html' { return 'webview' }
        '.js'   { return 'webview' }
        '.css'  { return 'webview' }
        '.png'  { return 'resource' }
        '.svg'  { return 'resource' }
        '.txt'  { return 'license' }
        default { return 'data' }
    }
}

$assets = foreach ($file in @($files)) {
    $rel = $file.FullName.Substring($PublishRoot.Length).TrimStart('\','/').Replace('\','/')
    [pscustomobject]@{
        relativePath = $rel.Replace('/', [IO.Path]::DirectorySeparatorChar)
        sizeBytes    = $file.Length
        sha256       = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        kind         = Get-AssetKind -RelativePath $rel
    }
}

# Deterministic fingerprint over the sorted, normalised entry set.
$jsonForHash = ($assets | Sort-Object relativePath | ConvertTo-Json -Depth 5 -Compress)
$fingerprint = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($jsonForHash)
    )
).Replace('-', '').ToLowerInvariant()

$manifest = [pscustomobject]@{
    schemaVersion       = 'invoiceflow.release-manifest.v1'
    productName         = $ProductName
    productVersion      = $ProductVersion
    runtimeIdentifier   = $RuntimeIdentifier
    architecture        = $Architecture
    assets              = @($assets | Sort-Object relativePath)
    manifestFingerprint = $fingerprint
}

$dir = Split-Path -Parent $Output
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$json = ($manifest | ConvertTo-Json -Depth 5) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($Output, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Wrote release manifest: $Output"
Write-Host "  product: $ProductName $ProductVersion"
Write-Host "  RID: $RuntimeIdentifier ($Architecture)"
Write-Host "  assets: $(@($assets).Count)"
Write-Host "  fingerprint: $fingerprint"