# URL Provider Strategies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace provider-family conditionals in the bounded recovery client with fixture-tested Nuonuo, direct-invoice, and Baiwang strategies that capture multiple artifacts and select one primary result.

**Architecture:** Provider strategies consume the grouped input and artifact contracts from `2026-09-24-url-recovery-contracts-plan.md`. Every strategy shares `PublicUrlPolicy` and `IUrlRecoveryTransport`; provider-specific request protocols and selectors stay isolated, while all accepted captures are passed to the common result contract.

**Tech Stack:** .NET SDK 10.0.401, C# 14, `SocketsHttpHandler`, `System.Text.Json`, ZIP APIs, xUnit 2, FluentAssertions, existing Python fixtures as behavioral references.

**Implementation Status (2026-09-24):** Tasks 2-4 are implemented and fixture-tested; Task 1's strategy registry and DI dispatch are implemented, but HTML-to-browser delegation is deferred because browser fallback remains disabled. Recent selector parity fixes cover direct seller case sensitivity and Baiwang amount precision. Provider-focused and solution tests pass (420 total). Commit steps are intentionally not performed.

## Global Constraints

- Do not change `global.json`; SDK `10.0.401` is pinned with roll-forward disabled.
- Do not make live provider calls in tests; use fake transport responses and synthetic values.
- Every request/redirect continues through `PublicUrlPolicy` and pinned transport.
- Never retry policy rejection or caller cancellation; capability-bearing POST fields never cross origins.
- Supported kind detection is PDF/XML/OFD plus ZIP containers whose members are independently validated.
- Selection returns at most one primary result; retain Python's direct XML-only fallback branches.

---

### Task 1: Add strategy dispatch and preserve the current generic fallback (Partial)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/IUrlRecoveryStrategy.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/UrlRecoveryStrategyRegistry.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Url/PublicUrlRecoveryClient.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryStrategyRegistryTests.cs`

**Interfaces:** The current `IUrlRecoveryStrategy` exposes `ProviderFamilies` and `RecoverAsync(UrlCandidateGroup, CancellationToken)`; the original `CanHandle` sketch below is superseded. The registry dispatches exactly one provider strategy. Unknown groups use the generic direct-artifact strategy; HTML/JavaScript delegation to a browser strategy is deferred until the browser egress boundary is proven.

- [x] **Step 1: Add registry tests for each registered family and generic fallback**

```csharp
[Fact]
public async Task Dispatches_known_family_to_one_strategy()
{
    // Arrange UrlRecoveryStrategyRegistry with one fake Nuonuo handler and one
    // fake generic handler, then pass a group whose ProviderFamily is Nuonuo.
    await registry.RecoverAsync(group with { ProviderFamily = "nuonuo_scan_invoice" }, CancellationToken.None);

    nuonuo.Calls.Should().Be(1);
    generic.Calls.Should().Be(0);
}
```

- [x] **Step 2: Run registry tests and verify dispatch types are missing**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryStrategyRegistryTests`

Expected: FAIL because the strategy registry has not been added.

- [x] **Step 3: Implement deterministic strategy dispatch and DI registration**

```csharp
public interface IUrlRecoveryStrategy
{
    bool CanHandle(string providerFamily);
    Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken);
}
```

Reject duplicate handlers for a family during registry construction; do not fall back to raw HTTP after a known provider strategy fails.

- [x] **Step 4: Run registry and existing bounded transport tests**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter "FullyQualifiedName~UrlRecoveryStrategyRegistryTests|FullyQualifiedName~PublicUrlRecoveryClientTests"`

Expected: PASS; generic redirects retain existing policy enforcement.

- [ ] **Step 5: Commit the strategy dispatch boundary**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/IUrlRecoveryStrategy.cs src/InvoiceFlowAI.Infrastructure/Url/UrlRecoveryStrategyRegistry.cs src/InvoiceFlowAI.Infrastructure/Url/PublicUrlRecoveryClient.cs src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryStrategyRegistryTests.cs
git commit -m "refactor: dispatch URL recovery by provider strategy"
```

### Task 2: Port Nuonuo artifact capture and selector behavior (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/NuonuoScanRecoveryStrategy.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Url/PublicUrlRecoveryClient.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlRecoveryClientTests.cs`

- [x] **Step 1: Add tests for PDF+XML captures, multiple source URLs, XML-only fallback, and cross-origin POST rejection**

```csharp
[Fact]
public async Task Captures_all_valid_nuonuo_kinds_but_selects_one_primary()
{
    // Construct NuonuoScanRecoveryStrategy with the test class's fake transport
    // and a group containing one synthetic short-link candidate.
    var result = await strategy.RecoverAsync(group, CancellationToken.None);

    result.Artifacts.Select(item => item.Kind).Should().Contain([RecoveredArtifactKind.Xml, RecoveredArtifactKind.Pdf]);
    result.SelectedArtifact!.Kind.Should().Be(RecoveredArtifactKind.Pdf);
}
```

- [x] **Step 2: Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~Nuonuo` and confirm the new multi-capture assertion fails**

Expected: FAIL because the current client discards XML/OFD and returns only bytes.

- [x] **Step 3: Move GET short-link, detail POST, returned URL enumeration, and signature checks into `NuonuoScanRecoveryStrategy`**

```csharp
foreach (var target in detailTargets)
    captures.AddRange(await _directArtifactProbe.ProbeAsync(target, sourceOrdinal, cancellationToken));

return _selector.SelectDirect(captures, group.ExpectedFields);
```

Probe all URLs in discovery order, capture valid PDF/XML/OFD, preserve `isOuterPageReq` endpoint selection, and retain the Python direct selector's matched-XML and single-XML fallback behavior.

- [x] **Step 4: Run all Nuonuo-focused tests and verify form fields never cross origin**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~Nuonuo`

Expected: PASS for protocol, selection, failure codes, and redirect guards.

- [ ] **Step 5: Commit Nuonuo strategy parity**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/NuonuoScanRecoveryStrategy.cs src/InvoiceFlowAI.Infrastructure/Url/PublicUrlRecoveryClient.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlRecoveryClientTests.cs
git commit -m "feat: capture Nuonuo recovery artifacts"
```

### Task 3: Port shared direct-provider probing, retries, ZIP extraction, and Fpyun redirects (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/DirectInvoiceRecoveryStrategy.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/DirectArtifactProbe.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/DirectInvoiceArtifactSelector.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlRecoveryClientTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlPolicyTests.cs`

- [x] **Step 1: Add fake-transport tests for three attempts, no retry on policy rejection, ZIP member limits, invoice-number match, and multiple-PDF ambiguity**

```csharp
[Fact]
public async Task Selects_matching_pdf_from_multiple_captured_artifacts()
{
    // Arrange three signature-valid captured artifacts and call the direct
    // selector using expected invoice number 11111111111111111111.
    var result = selector.Select(expectedFields, [wrongPdf, matchingXml, matchingPdf]);

    result.Artifacts.Should().HaveCount(3);
    result.SelectedArtifact.Should().BeSameAs(matchingPdf);
}
```

- [x] **Step 2: Run direct-provider tests and confirm retry and ZIP cases fail**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~DirectInvoice`

Expected: FAIL because the current path performs one GET and does not unpack archives.

- [x] **Step 3: Implement shared bounded probing and the Python selector rules**

Expose `DirectInvoiceArtifactSelector.Select(IReadOnlyDictionary<string, string> expectedFields, IReadOnlyList<CapturedUrlArtifact> captures)` and return a `UrlRecoveryResult` with all accepted captures plus one selected index. Throw a typed `UrlRecoveryException` with a stable safe reason code when selection fails.

```csharp
for (var attempt = 0; attempt < 3; attempt++)
{
    var attemptResult = await ProbeOnceAsync(candidate, cancellationToken);
    captures.AddRange(attemptResult.Artifacts);
    if (attemptResult.Artifacts.Count > 0 || attemptResult.IsPolicyFailure) break;
    await delay.DelayAsync(attempt, cancellationToken);
}
```

For ZIP payloads, cap archive member count, total expanded bytes, and per-member bytes before extraction; only emit signature-validated PDF/XML/OFD members. Implement Fpyun's exact approved redirect stages and require redirect query equality; never relax `PublicUrlPolicy` for other hosts.

- [x] **Step 4: Run direct strategy, URL policy, and archive-limit tests**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DirectInvoice|FullyQualifiedName~PublicUrlPolicyTests"`

Expected: PASS for ChinaTax, BWJF, Fpyun, PDD, JDCloud, and KPByd family dispatch with synthetic endpoints.

- [ ] **Step 5: Commit shared direct-provider behavior**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/DirectInvoiceRecoveryStrategy.cs src/InvoiceFlowAI.Infrastructure/Url/DirectArtifactProbe.cs src/InvoiceFlowAI.Infrastructure/Url/DirectInvoiceArtifactSelector.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlRecoveryClientTests.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlPolicyTests.cs
git commit -m "feat: recover direct invoice provider artifacts"
```

### Task 4: Port Baiwang wrapper and multi-artifact selection (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/BaiwangRecoveryStrategy.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/BaiwangArtifactSelector.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/BaiwangRecoveryStrategyTests.cs`

- [x] **Step 1: Add tests for direct wrapper PDF, preview API XML/PDF/OFD downloads, wrapper-PDF rejection, matched XML metadata, and ambiguous PDFs**

```csharp
[Fact]
public async Task Uses_matching_xml_to_select_the_related_pdf_without_emitting_xml_as_primary()
{
    // Construct BaiwangRecoveryStrategy with fake preview and download
    // responses for one PDF and one XML artifact.
    var result = await strategy.RecoverAsync(group, CancellationToken.None);

    result.Artifacts.Should().Contain(item => item.Kind == RecoveredArtifactKind.Xml);
    result.SelectedArtifact!.Kind.Should().Be(RecoveredArtifactKind.Pdf);
}
```

- [x] **Step 2: Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~BaiwangRecoveryStrategyTests` and confirm expected strategy tests fail**

Expected: FAIL because Baiwang currently uses generic GET and PDF validation.

- [x] **Step 3: Implement Baiwang preview API and artifact selector**

```csharp
var preview = await _transport.SendAsync(postRequest, cancellationToken);
if (!preview.Success) continue;
foreach (var kind in BaiwangFormatOrder)
    captures.AddRange(await _artifactProbe.ProbeFormatAsync(preview.Param, kind, cancellationToken));
```

Use only fixed `pis.baiwang.com` endpoints and query/form fields from the validated candidate; parse XML fields with size-bounded XML settings; reject wrapper PDFs and apply the Python match/ambiguity rules before selecting one primary artifact.

- [x] **Step 4: Run Baiwang tests and full Infrastructure tests**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`

Expected: PASS; no test performs a live provider request.

- [ ] **Step 5: Commit Baiwang strategy and fixture tests**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/BaiwangRecoveryStrategy.cs src/InvoiceFlowAI.Infrastructure/Url/BaiwangArtifactSelector.cs src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/BaiwangRecoveryStrategyTests.cs
git commit -m "feat: recover Baiwang invoice artifacts"
```

## Final Verification

Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj` and `dotnet test InvoiceFlowAI.sln`. Fixture outcomes must match Python selector reasons and selected artifact kind; live network access is not allowed in tests.
