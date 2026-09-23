# model-manifest.ps1 — Task 11 step 3.
#
# Walks the model assets directory emitted by publish (or, in CI,
# a frozen -ModelsRoot path) and writes a deterministic JSON
# manifest of every file. The output schema mirrors the C#
# `ModelManifest` record so the runtime check in
# ReleaseManifestVerifier.VerifyModel can consume it directly.
#
# Usage:
#   pwsh -File build/model-manifest.ps1 `
#        -ModelsRoot publish/win-x64/manifests `
#        -Output publish/win-x64/manifests/model.json

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModelsRoot,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ModelsRoot -PathType Container)) {
    throw "ModelsRoot '$ModelsRoot' does not exist or is not a directory."
}

# Stable sort by relative path so the manifest JSON hash is
# reproducible regardless of the OS's file enumeration order.
$files = Get-ChildItem -LiteralPath $ModelsRoot -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/]manifests[\\/]' } |
    Sort-Object -Property @{ Expression = { $_.FullName.Substring($ModelsRoot.Length).TrimStart('\','/') } }

$assets = foreach ($file in @($files)) {
    $rel = $file.FullName.Substring($ModelsRoot.Length).TrimStart('\','/').Replace('\','/')
    $kind = switch -Wildcard ($rel) {
        '*.bin'        { 'model' }
        '*.onnx'       { 'onnx' }
        '*.json'       { 'metadata' }
        '*.txt'        { 'license' }
        default        { 'data' }
    }
    [pscustomobject]@{
        relativePath = $rel.Replace('/', [IO.Path]::DirectorySeparatorChar)
        sizeBytes    = $file.Length
        sha256       = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        kind         = $kind
        revision     = $null
        notes        = $null
    }
}

$vendor = 'InvoiceFlowAI'
$schema = 'invoiceflow.model-manifest.v1'

# Compute the manifest fingerprint deterministically (sorted entries).
$jsonForHash = ($assets | Sort-Object relativePath | ConvertTo-Json -Depth 5 -Compress)
$fingerprint = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($jsonForHash)
    )
).Replace('-', '').ToLowerInvariant()

$manifest = [pscustomobject]@{
    schemaVersion       = $schema
    vendor              = $vendor
    assets              = @($assets | Sort-Object relativePath)
    manifestFingerprint = $fingerprint
}

$dir = Split-Path -Parent $Output
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

# Use LF line endings + UTF-8 no-BOM so the manifest is portable.
$json = ($manifest | ConvertTo-Json -Depth 5) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($Output, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Wrote model manifest: $Output"
Write-Host "  vendor: $vendor"
Write-Host "  assets: $(@($assets).Count)"
Write-Host "  fingerprint: $fingerprint"