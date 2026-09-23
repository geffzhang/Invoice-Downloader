# .NET 10 Migration Toolchain Blockers — 2026-09-23

> Status captured during the RED phase of Task 1 of `2026-09-23-dotnet10-migration-implementation-plan.md`.
> Updated when tools change on this machine.

This document records the **exact** toolchain state on the agent environment that ran the
verification step, and the gap between that state and the plan-mandated pins. It is an
execution artifact, not a plan amendment.

## Verified tool versions

| Tool | Required by plan | Detected on this machine | Status |
|---|---|---|---|
| .NET SDK | `10.0.100` (rollForward=disable, allowPrerelease=false) | `10.0.100` at `~/.dotnet/sdk/10.0.100` (also `10.0.401` at `C:\Program Files\dotnet\sdk`) | PASS |
| WiX Toolset | `5.0.2` | `5.0.2+aa65968c` via `dotnet tool install wix --global --version 5.0.2` (binary at `~/.dotnet/tools/wix`) | PASS |
| MSVC v143 | `14.44.35207` | `14.51.36231` at `C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\` (newer; VS 18 Community, not VS 2022) | Drift (newer) |
| Windows SDK | `10.0.26100.1` | No `10.0.26100.1` include directory. Only `C:\Program Files\Microsoft Visual Studio\18\Community\SDK\ScopeCppSDK\vc15\SDK\include\{shared,ucrt,um}` (bundled with VS 18, partial, no exposed version). `winget install Microsoft.WindowsSDK.10.0.26100 --version 10.0.26100.7705` cached `winsdksetup.exe` but the bootstrapper exited 0 without installing components when run `/quiet` non-elevated. Registry `HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots` points `KitsRoot10` to `D:\Windows Kits\10\` (non-existent drive). | Gap (not installed) |

## What was attempted

1. `dotnet-install.ps1 -Version 10.0.100 -InstallDir $HOME/.dotnet` → installed SDK 10.0.100 with runtimes `Microsoft.NETCore.App/10.0.0`, `Microsoft.WindowsDesktop.App/10.0.0`, `Microsoft.AspNetCore.App/10.0.0`.
2. `dotnet tool install wix --version 5.0.2 --global` → installed `5.0.2+aa65968c`.
3. `winget install Microsoft.WindowsSDK.10.0.26100 --exact --version 10.0.26100.7705` → bootstrapper launched but left no installed components in `C:\Program Files (x86)\Windows Kits\10\`. Subsequent silent run with `winsdksetup.exe /quiet /features OptionId.*` exited 0 without effect.
4. Searched `VS 18 Community\SDK\ScopeCppSDK\vc15\SDK\include` for headers — `Windows.h` and `ucrt` exist; cannot be claimed as "10.0.26100.1" without a version metadata file.

## Why execution stops at Task 1 Step 2

The plan writes Task 1 as a TDD cycle: Step 1 writes `build/verify-toolchain.ps1`, Step 2
runs it and the plan **explicitly expects** failure with the message

> "在工具未安装或版本不匹配时返回非零结果，并显示具体原因。"

`pwsh build/verify-toolchain.ps1` returns exit `11` with the message
`TOOLCHAIN_FAIL: windows-sdk - Windows SDK 10.0.26100.1 is required, but available include directories: .`

and produces `artifacts/toolchain.json`. This satisfies Step 1 and Step 2 exactly.

Proceeding to Step 3 (scaffolding `InvoiceFlowAI.sln`, `global.json`, `Directory.Build.props`,
`src/InvoiceFlowAI.*/`, etc.) when the Windows SDK pin cannot be honored would violate the
plan's global constraint:

> "不接受'使用已安装的最近版本'。"

The plan also says the App (WinUI 3) project uses `Microsoft.WindowsAppSDK` from NuGet, so
the WinUI 3 surface may build against that without a system Windows SDK at exact 10.0.26100.1,
but the plan pins the system SDK regardless. Any deviation must be recorded in the plan and
approved before Task 1 Step 3 begins.

## Machine-readable record

`artifacts/toolchain.json` is the canonical machine-readable record of the verification run
on this machine. Re-run `pwsh -File build/verify-toolchain.ps1` after any tool change to
refresh it.
