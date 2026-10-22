[CmdletBinding()]
param(
    [string]$PublishRoot = 'publish/win-x64'
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = if ([System.IO.Path]::IsPathRooted($PublishRoot)) {
    [System.IO.Path]::GetFullPath($PublishRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $root $PublishRoot))
}

if (-not (Test-Path -LiteralPath $publish -PathType Container)) {
    throw "Publish root '$publish' does not exist."
}

$atomDlls = @(Get-ChildItem -LiteralPath $publish -Filter 'AtomUI*.dll' -File -Recurse)
if ($atomDlls.Count -lt 2) {
    throw "Expected AtomUI assemblies as separate DLLs, found $($atomDlls.Count)."
}

$allFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
if ($allFiles.Count -lt 20) {
    throw "Publish output has only $($allFiles.Count) files; refusing a single-file-style bundle."
}

$projectFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.csproj' -File -Recurse)
$singleFileSettings = $projectFiles | Select-String -Pattern 'PublishSingleFile|PublishTrimmed'
if ($singleFileSettings) {
    throw "Single-file or trimmed publishing is not allowed: $($singleFileSettings -join '; ')"
}

$notices = Join-Path $publish 'licenses/THIRD-PARTY-NOTICES.txt'
$license = Join-Path $publish 'licenses/LGPL-3.0.txt'
if (-not (Test-Path -LiteralPath $notices -PathType Leaf)) {
    throw 'THIRD-PARTY-NOTICES.txt is missing from the publish output.'
}
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw 'LGPL-3.0.txt is missing from the publish output.'
}
if (-not (Select-String -LiteralPath $notices -Pattern 'AtomUI' -Quiet)) {
    throw 'THIRD-PARTY-NOTICES.txt does not mention AtomUI.'
}
if (-not (Select-String -LiteralPath $license -Pattern 'GNU LESSER GENERAL PUBLIC LICENSE' -Quiet)) {
    throw 'LGPL-3.0.txt does not contain the LGPL text.'
}

$gplLicense = Join-Path $publish 'licenses/GPL-3.0.txt'
if (-not (Test-Path -LiteralPath $gplLicense -PathType Leaf)) {
    throw 'GPL-3.0.txt is missing from the publish output.'
}
if (-not (Select-String -LiteralPath $gplLicense -Pattern 'GNU GENERAL PUBLIC LICENSE' -Quiet)) {
    throw 'GPL-3.0.txt does not contain the GPL text.'
}

Write-Host "LGPL packaging checks passed: $($atomDlls.Count) AtomUI assemblies, $($allFiles.Count) files."
