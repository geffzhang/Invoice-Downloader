<#
.SYNOPSIS
    Verify that the InvoiceFlowAI .NET 10 migration toolchain matches the exact versions
    mandated by docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md and the
    implementation plan, then emit a machine-readable `artifacts/toolchain.json`.

.DESCRIPTION
    The plan's global constraint forbids "use the closest installed version". The script
    returns non-zero on the first missing or version-mismatched prerequisite, and prints
    a stable error code prefixed with `TOOLCHAIN_*` for CI to surface.

    Pinned versions (sourced from the plan and design spec):
      * .NET SDK               10.0.100
      * Windows SDK            10.0.26100.1
      * MSVC v143              14.44.35207
      * WiX Toolset            5.0.2

    The script only reads version metadata and never mutates the host. It is safe to run
    on developer workstations and on CI runners before any restore/build step.

.PARAMETER RequiredDotnetSdkVersion
    The exact SDK version required by the migration plan. Default: 10.0.100.

.PARAMETER RequiredWindowsSdkVersion
    The exact Windows SDK version required. Default: 10.0.26100.1.

.PARAMETER RequiredMsvcVersion
    The exact MSVC v143 toolset version required. Default: 14.44.35207.

.PARAMETER RequiredWixVersion
    The exact WiX Toolset major.minor.patch required. Default: 5.0.2.

.PARAMETER JsonOutputPath
    Where to write the machine-readable result. Default: artifacts/toolchain.json.

.EXAMPLE
    pwsh -File build/verify-toolchain.ps1

.NOTES
    Exit codes:
      0   - all required tools match the exact mandated versions
      10  - .NET SDK missing or wrong version
      11  - Windows SDK missing or wrong version
      12  - MSVC v143 missing or wrong version
      13  - WiX Toolset missing or wrong version
      20  - unexpected exception
#>

param(
    [string]$RequiredDotnetSdkVersion = "10.0.100",
    [string]$RequiredWindowsSdkVersion = "10.0.26100.1",
    [string]$RequiredMsvcVersion = "14.44.35207",
    [string]$RequiredWixVersion = "5.0.2",
    [string]$JsonOutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir ".."))
if ([string]::IsNullOrWhiteSpace($JsonOutputPath)) {
    $JsonOutputPath = Join-Path $RepoRoot "artifacts/toolchain.json"
}

$results = New-Object System.Collections.Generic.List[object]

function Write-Result {
    param(
        [string]$Tool,
        [string]$Required,
        [string]$Actual,
        [bool]$Matched,
        [string]$Notes = ""
    )
    $obj = [ordered]@{
        tool     = $Tool
        required = $Required
        actual   = $Actual
        matched  = $Matched
        notes    = $Notes
    }
    $script:results.Add([pscustomobject]$obj) | Out-Null
}

function Fail {
    param(
        [int]$ExitCode,
        [string]$Tool,
        [string]$Message
    )
    Write-Host "TOOLCHAIN_FAIL: $Tool - $Message" -ForegroundColor Red
    $summary = [ordered]@{
        toolchain_ok = $false
        failing_tool = $Tool
        message      = $Message
        results      = $script:results
    }
    Write-ToolchainJson -Payload $summary
    exit $ExitCode
}

function Write-ToolchainJson {
    param([hashtable]$Payload)
    $jsonDir = Split-Path -Parent $JsonOutputPath
    if (-not (Test-Path $jsonDir)) {
        New-Item -ItemType Directory -Force -Path $jsonDir | Out-Null
    }
    $serialized = $Payload | ConvertTo-Json -Depth 6
    Set-Content -LiteralPath $JsonOutputPath -Value $serialized -Encoding UTF8
}

function Get-DotnetInstallRoots {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += $env:DOTNET_ROOT }
    $candidates += @(
        "C:\Program Files\dotnet",
        "C:\Program Files (x86)\dotnet",
        (Join-Path $env:USERPROFILE ".dotnet")
    )
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate "dotnet.exe"))) {
            $found.Add($candidate)
        }
    }
    return @($found)
}

function Get-InstalledDotnetSdks {
    $roots = Get-DotnetInstallRoots
    if ($roots.Count -eq 0) {
        return @()
    }
    $all = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        $dotnetExe = Join-Path $root "dotnet.exe"
        try {
            $lines = & $dotnetExe --list-sdks 2>&1
            foreach ($line in $lines) {
                $trimmed = ([string]$line).Trim()
                if (-not $trimmed) { continue }
                # Lines look like: "10.0.401 [C:\Program Files\dotnet\sdk]". Take only the leading version token.
                $versionToken = ($trimmed -split '\s+')[0]
                if ($versionToken -and $versionToken -match '^[0-9]+\.[0-9]+\.[0-9]+') {
                    $all.Add($versionToken)
                }
            }
        } catch {
        }
    }
    return @($all | Select-Object -Unique)
}

function Resolve-DotnetExe {
    # Prefer the on-PATH dotnet if present, else the first known install root.
    $dotnetExe = (Get-Command dotnet -ErrorAction SilentlyContinue)
    if ($dotnetExe -and $dotnetExe.Source) {
        return $dotnetExe.Source
    }
    $roots = Get-DotnetInstallRoots
    if ($roots.Count -gt 0) {
        return (Join-Path $roots[0] "dotnet.exe")
    }
    return $null
}

function Test-DotnetSdk {
    $installed = Get-InstalledDotnetSdks
    if ($installed.Count -eq 0) {
        Write-Result -Tool "dotnet-sdk" -Required $RequiredDotnetSdkVersion -Actual "<not found>" -Matched $false -Notes "dotnet.exe not present on PATH or DOTNET_ROOT"
        Fail -ExitCode 10 -Tool "dotnet-sdk" -Message ".NET SDK not found. Install $RequiredDotnetSdkVersion via https://dot.net/v1/dotnet-install.ps1."
    }
    $matched = $installed -contains $RequiredDotnetSdkVersion
    if (-not $matched) {
        Write-Result -Tool "dotnet-sdk" -Required $RequiredDotnetSdkVersion -Actual ($installed -join ",") -Matched $false -Notes "Required exact version not installed"
        Fail -ExitCode 10 -Tool "dotnet-sdk" -Message ".NET SDK $RequiredDotnetSdkVersion is required, but installed: $($installed -join ', ')."
    }
    $dotnetExe = Resolve-DotnetExe
    Write-Result -Tool "dotnet-sdk" -Required $RequiredDotnetSdkVersion -Actual $RequiredDotnetSdkVersion -Matched $true -Notes "$dotnetExe (consolidated across $(@(Get-DotnetInstallRoots).Count) install root(s))"
}

function Test-WindowsSdk {
    $kitsRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\Include",
        "C:\Program Files\Windows Kits\10\Include"
    )
    $installed = @()
    foreach ($root in $kitsRoots) {
        if (Test-Path $root) {
            $installed += Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
        }
    }
    # Fallback: VS 2025 ScopeCppSDK bundle. Treated as informational only.
    $vsBundled = Test-Path "C:\Program Files\Microsoft Visual Studio\18\Community\SDK\ScopeCppSDK\vc15\SDK\include\um\Windows.h"
    if (($installed -contains $RequiredWindowsSdkVersion)) {
        Write-Result -Tool "windows-sdk" -Required $RequiredWindowsSdkVersion -Actual $RequiredWindowsSdkVersion -Matched $true -Notes "Kits include directory"
        return
    }
    $note = "Installed Windows SDK includes: $(($installed | Select-Object -Unique) -join ',')"
    if ($vsBundled) { $note += "; VS 2025 ScopeCppSDK headers detected (cannot be used to claim an exact $RequiredWindowsSdkVersion match)" }
    Write-Result -Tool "windows-sdk" -Required $RequiredWindowsSdkVersion -Actual (($installed | Select-Object -Unique) -join ",") -Matched $false -Notes $note
    Fail -ExitCode 11 -Tool "windows-sdk" -Message "Windows SDK $RequiredWindowsSdkVersion is required, but available include directories: $(($installed | Select-Object -Unique) -join ', ')."
}

function Test-Msvc {
    $msvcRoots = @(
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC",
        "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC",
        "C:\BuildTools\VC\Tools\MSVC"
    )
    $installed = @()
    foreach ($root in $msvcRoots) {
        if (Test-Path $root) {
            $installed += Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
        }
    }
    if ($installed -contains $RequiredMsvcVersion) {
        Write-Result -Tool "msvc-v143" -Required $RequiredMsvcVersion -Actual $RequiredMsvcVersion -Matched $true -Notes "v143 toolset"
        return
    }
    Write-Result -Tool "msvc-v143" -Required $RequiredMsvcVersion -Actual ($installed -join ",") -Matched $false -Notes "Required exact v143 toolset not installed"
    Fail -ExitCode 12 -Tool "msvc-v143" -Message "MSVC v143 $RequiredMsvcVersion is required, but installed: $($installed -join ', ')."
}

function Test-Wix {
    $candidates = @()
    $wixCommand = Get-Command wix -ErrorAction SilentlyContinue
    if ($wixCommand -and $wixCommand.Source) {
        try {
            $rawVersion = (& $wixCommand.Source --version 2>&1 | Out-String).Trim()
            $candidates += $rawVersion
        } catch {
        }
    }
    $installedDisplay = ($candidates | Select-Object -Unique) -join ","
    $matched = $false
    foreach ($candidate in $candidates) {
        if ($candidate -like "$RequiredWixVersion*") {
            $matched = $true
            break
        }
    }
    if ($matched) {
        Write-Result -Tool "wix" -Required $RequiredWixVersion -Actual ($candidates[0]) -Matched $true -Notes ($wixCommand.Source)
        return
    }
    Write-Result -Tool "wix" -Required $RequiredWixVersion -Actual $installedDisplay -Matched $false -Notes "Install via 'dotnet tool install wix --version $RequiredWixVersion --global'"
    Fail -ExitCode 13 -Tool "wix" -Message "WiX Toolset $RequiredWixVersion is required, but installed: $installedDisplay."
}

# Body
Test-DotnetSdk
Test-WindowsSdk
Test-Msvc
Test-Wix

$summary = [ordered]@{
    toolchain_ok = $true
    failing_tool = $null
    results      = $results
}
Write-ToolchainJson -Payload $summary
Write-Host "TOOLCHAIN_OK: all required tools matched" -ForegroundColor Green
exit 0
