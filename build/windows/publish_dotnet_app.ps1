[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$PublishRoot = "publish\win-x64",
    [string]$ProductVersion = "1.0.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$AppProject = Join-Path $RepoRoot "src\InvoiceFlowAI.App\InvoiceFlowAI.App.csproj"
$WorkerProject = Join-Path $RepoRoot "src\InvoiceFlowAI.UrlRecovery.Worker\InvoiceFlowAI.UrlRecovery.Worker.csproj"
$OutputRoot = if ([System.IO.Path]::IsPathRooted($PublishRoot)) {
    [System.IO.Path]::GetFullPath($PublishRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $PublishRoot))
}
$WorkerStage = Join-Path ([System.IO.Path]::GetTempPath()) ("InvoiceFlowAI-worker-publish-" + [guid]::NewGuid().ToString("N"))

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
try {
    Invoke-Dotnet @("publish", $AppProject, "--configuration", $Configuration, "--runtime", "win-x64", "--self-contained", "true", "--output", $OutputRoot)
    Invoke-Dotnet @("publish", $WorkerProject, "--configuration", $Configuration, "--runtime", "win-x64", "--self-contained", "true", "--output", $WorkerStage)

    foreach ($Name in @(
        "InvoiceFlowAI.UrlRecovery.Worker.exe",
        "InvoiceFlowAI.UrlRecovery.Worker.dll",
        "InvoiceFlowAI.UrlRecovery.Worker.deps.json",
        "InvoiceFlowAI.UrlRecovery.Worker.runtimeconfig.json"
    )) {
        $Source = Join-Path $WorkerStage $Name
        if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
            throw "Worker publish output is missing a required sidecar file."
        }
        Copy-Item -LiteralPath $Source -Destination (Join-Path $OutputRoot $Name) -Force
    }

    $ManifestDirectory = Join-Path $OutputRoot "manifests"
    $ModelsDirectory = Join-Path $OutputRoot "models"
    New-Item -ItemType Directory -Force -Path $ManifestDirectory, $ModelsDirectory | Out-Null
    & (Join-Path $RepoRoot "build\model-manifest.ps1") -ModelsRoot $ModelsDirectory -Output (Join-Path $ManifestDirectory "model.json")
    & (Join-Path $RepoRoot "build\release-manifest.ps1") -PublishRoot $OutputRoot -Output (Join-Path $ManifestDirectory "release.json") -ProductVersion $ProductVersion -RuntimeIdentifier "win-x64" -Architecture "x64"
}
finally {
    if (Test-Path -LiteralPath $WorkerStage) {
        Remove-Item -LiteralPath $WorkerStage -Recurse -Force
    }
}