# Provider Recovery Parity Implementation Plan

> **For agentic workers:** Execute each task with a focused failing test first, then the smallest implementation change. Do not commit; the user explicitly requested no commits.

**Goal:** Close the remaining Nuonuo, Baiwang, direct-provider, and URL-security parity rows using deterministic C# tests and synthetic data.

**Architecture:** Keep recovery decisions in the existing provider strategies, `PublicUrlRecoveryClient`, `PublicUrlPolicy`, and `PinnedHttpUrlRecoveryTransport`. Exercise provider recovery with fake transports and test the socket transport separately without live public-network dependencies. Modify production behavior only when a focused test demonstrates a gap.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, `IUrlRecoveryTransport`, `PublicUrlPolicy`.

## Global Constraints

- No live-network test requests.
- Do not place credentials, signed URL queries, real mailbox data, or raw exception text in fixtures or assertions that model public output.
- Preserve stable safe reason codes and test redaction explicitly.
- Do not commit.

---

### Task 1: Nuonuo Recovery Outcomes

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/NuonuoScanRecoveryStrategyTests.cs`
- Modify only if a test fails for a production defect: `src/InvoiceFlowAI.Infrastructure/Url/NuonuoScanRecoveryStrategy.cs`
- Fixture: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity/provider-recovery.json`

- [ ] Add synthetic cases for invalid JSON, provider status failure, absent artifact URLs, XML-only response, duplicate artifact URLs, and PDF download failure after a valid XML download.
- [ ] Run only `NuonuoScanRecoveryStrategyTests` and confirm new expectations fail for the missing parity behavior.
- [ ] Implement only the locally demonstrated behavior while preserving the rule that the selected recovered artifact must be a signature-valid PDF.
- [ ] Re-run `NuonuoScanRecoveryStrategyTests` and the Nuonuo cases in `PublicUrlRecoveryClientTests`.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~NuonuoScanRecoveryStrategyTests`
Expected: all selected tests pass.

### Task 2: Baiwang Strategy Selection

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/BaiwangRecoveryStrategyTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity/provider-recovery.json`
- Modify only if a test fails for a production defect: `src/InvoiceFlowAI.Infrastructure/Url/BaiwangRecoveryStrategy.cs` and `BaiwangArtifactSelector.cs`

- [ ] Add a recovery-strategy fixture for OFD selection and same-source wrapper/PDF handling, including a mismatch case that must not select the artifact.
- [ ] Run only `BaiwangRecoveryStrategyTests` and confirm the new assertions expose any missing behavior.
- [ ] Apply the smallest selector/strategy fix supported by the failing fixture.
- [ ] Re-run Baiwang strategy and selector tests.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~BaiwangRecoveryStrategyTests`
Expected: all selected tests pass.

### Task 3: Direct Provider Matrix

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/DirectArtifactProbeTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlRecoveryClientTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity/provider-recovery.json`
- Modify only if a test fails for a production defect: `DirectArtifactProbe.cs` or `DirectInvoiceArtifactSelector.cs`

- [ ] Parameterize the six direct provider families (ChinaTax, BWJF, Fpyun, PDD, JD, KPBYD) over a signature-valid PDF success, transient retry, malformed/non-document response, and non-retryable policy rejection.
- [ ] Add the Python-observed PDF/OFD/XML identity-selection cases where that family supports them; assert selected ordinal, identity reason code, and opaque/sanitized origin only.
- [ ] Run only direct-probe/client tests and verify the new cases fail before implementation changes.
- [ ] Fix only demonstrated recovery/selection mismatches, then re-run the selected tests.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DirectArtifactProbeTests|FullyQualifiedName~DirectInvoiceArtifactSelectorTests|FullyQualifiedName~PublicUrlRecoveryClientTests`
Expected: all selected tests pass.

### Task 4: URL Security and Transport Lifecycle

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlPolicyTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlRecoveryClientTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PinnedHttpUrlRecoveryTransportTests.cs` (create if absent)
- Modify only if a test fails for a production defect: `PublicUrlPolicy.cs`, `PublicUrlRecoveryClient.cs`, `PinnedHttpUrlRecoveryTransport.cs`

- [ ] Add a redirect-chain test that changes from a public hostname to a DNS-private hostname and asserts no request is sent to the rejected hop.
- [ ] Add cancellation/read-failure tests using an injected local transport seam; assert bounded body behavior and deterministic resource disposal without reaching public hosts.
- [ ] Assert protected POST fields are not forwarded cross-origin and safe failures do not expose hostname, path, query, token, or raw exception content.
- [ ] Test the configured proxy behavior as implemented: transport must not inherit a system proxy and must connect only to a validated pinned peer. Do not add support for untrusted proxying.
- [ ] Run selected URL tests; change production code only for failing assertions, then run all `Url` tests.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~PublicUrlPolicyTests|FullyQualifiedName~PublicUrlRecoveryClientTests|FullyQualifiedName~PinnedHttpUrlRecoveryTransportTests`
Expected: all selected tests pass.

### Task 5: Matrix Evidence

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`

- [ ] Change provider recovery/security rows only where the preceding focused tests now assert the complete row behavior.
- [ ] Leave any unsupported or untested edge marked `partial` or `decision needed` with the exact remaining edge.
- [ ] Run all `Url` tests and `DesktopParityFixtureTests` before changing a row to `verified`.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Url|FullyQualifiedName~DesktopParityFixtureTests`
Expected: all selected tests pass; no matrix row is promoted without matching test evidence.
