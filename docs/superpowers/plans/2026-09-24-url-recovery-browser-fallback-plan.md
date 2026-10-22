# URL Recovery Browser Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Playwright recovery for generic JavaScript-generated invoice downloads without allowing the browser process to bypass the URL policy.

**Architecture:** This plan depends on the worker and manifest boundary in `2026-09-24-url-recovery-worker-integration-plan.md`. Launch Chromium only inside that worker, configure every request through a local enforcing egress proxy, and block direct browser egress at the process/host boundary. If that enforcement cannot be demonstrated on the supported Windows runtime, keep the browser strategy disabled and ship only the verified disabled state.

**Tech Stack:** .NET SDK 10.0.401, existing `Microsoft.Playwright` 1.62.0 package, `TcpListener`/`Socket`, `PublicUrlPolicy`, worker protocol from the worker integration plan, xUnit 2.

**Implementation Status (2026-09-24):** Browser proxy, Playwright strategy, and Windows process sandbox are not implemented. Per the fail-closed requirement, browser fallback remains unregistered; a production DI regression test verifies that disabled state. No browser proxy or direct-egress sandbox test has been run. This plan is intentionally incomplete pending a proven Windows egress boundary.

## Global Constraints

- Do not add another Playwright package; Infrastructure already references `Microsoft.Playwright` centrally.
- `PublicUrlPolicy` remains the authority for scheme, host, port, DNS, public address, and peer validation.
- The browser receives no direct network route; all HTTP, HTTPS CONNECT, redirects, subresources, downloads, and WebSockets use the enforcing proxy.
- Disable QUIC, service workers, WebRTC direct candidates, proxy bypass lists, and downloads not observed by the recovery strategy.
- Reject unsupported schemes, non-public destinations, proxy authentication, or policy initialization failure before navigation begins.
- Browser limits include total wall time, one navigation chain, request count, concurrent connections, total downloaded bytes, and final artifact bytes.
- Browser fallback is registered only after a test proves that a forbidden destination is rejected before the upstream socket is opened.

---

### Task 1: Prove a policy-enforcing proxy boundary before enabling Playwright (Not Started)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/Browser/UrlRecoveryEgressProxy.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/Browser/UrlRecoveryProxyConnection.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryEgressProxyTests.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Url/PublicUrlPolicy.cs` only if a missing injectable resolver/connector seam is needed

**Interfaces:** `UrlRecoveryEgressProxy.StartAsync(CancellationToken)` returns an ephemeral loopback endpoint for Chromium configuration; every upstream connection is created through an injected `IValidatedPublicConnectionFactory` that consumes `ValidatedPublicUrl`.

- [ ] **Step 1: Add proxy tests for public CONNECT validation, DNS rebinding, loopback/private blocks, malformed authority, and byte quotas**

```csharp
[Fact]
public async Task Rejects_private_destination_before_opening_upstream_socket()
{
    var response = await proxy.SendConnectAsync("127.0.0.1:443");

    response.StatusCode.Should().Be(403);
    connector.OpenCallCount.Should().Be(0);
}
```

- [ ] **Step 2: Run proxy tests and verify they fail closed before any browser integration exists**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryEgressProxyTests`

Expected: FAIL because the proxy and validated connector are not implemented.

- [ ] **Step 3: Implement CONNECT and plain HTTP forwarding through the validated connector**

```csharp
var validated = await _policy.ValidateAsync(request.TargetUri, cancellationToken);
await using var upstream = await _connector.ConnectAsync(validated, cancellationToken);
await TunnelWithByteLimitAsync(clientStream, upstream, _limits, cancellationToken);
```

Reject userinfo, non-HTTP(S), unsupported ports, non-public resolutions, peer mismatch, excessive concurrent connections, and byte quota exhaustion. Do not add an allow-all or test-only public bypass to production registrations.

- [ ] **Step 4: Run proxy tests with fake DNS and connector implementations**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~UrlRecoveryEgressProxyTests`

Expected: PASS; denied destinations produce zero upstream connect calls.

- [ ] **Step 5: Commit the proxy boundary**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/Browser/UrlRecoveryEgressProxy.cs src/InvoiceFlowAI.Infrastructure/Url/Browser/UrlRecoveryProxyConnection.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryEgressProxyTests.cs src/InvoiceFlowAI.Infrastructure/Url/PublicUrlPolicy.cs
git commit -m "feat: enforce URL policy for browser egress"
```

### Task 2: Launch an isolated Playwright context through the proxy (Not Started)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/Browser/BrowserUrlRecoveryStrategy.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/Browser/BrowserRecoveryLimits.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/BrowserUrlRecoveryStrategyTests.cs`

- [ ] **Step 1: Add tests for JavaScript-triggered download, popup rejection, WebSocket rejection, redirect limits, and cancellation cleanup**

Use an in-process fixture server only through a test connector that explicitly injects its socket after the test policy approves the fixture target; production policy must continue to reject loopback destinations.

- [ ] **Step 2: Run browser strategy tests and confirm no generic browser strategy is registered**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~BrowserUrlRecoveryStrategyTests`

Expected: FAIL because the strategy is not implemented.

- [ ] **Step 3: Implement one-page recovery with strict Playwright launch options**

```csharp
var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true,
    Proxy = new Proxy { Server = proxy.Endpoint.ToString() },
    Args = ["--disable-quic", "--proxy-bypass-list=<-loopback>"]
});
```

Install page/context handlers before navigation. Deny unsupported schemes and unobserved downloads, enforce request-count and wall-time limits, save one candidate artifact into the worker job directory, validate its signature, and dispose context/browser/proxy in `finally` on every exit path.

- [ ] **Step 4: Run Playwright strategy tests and inspect proxy upstream counters**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~BrowserUrlRecoveryStrategyTests`

Expected: PASS; every allowed request traverses the proxy and forbidden requests never reach the upstream connector.

- [ ] **Step 5: Commit the isolated browser strategy**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/Browser/BrowserUrlRecoveryStrategy.cs src/InvoiceFlowAI.Infrastructure/Url/Browser/BrowserRecoveryLimits.cs src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/BrowserUrlRecoveryStrategyTests.cs
git commit -m "feat: add policy-bound browser URL recovery"
```

### Task 3: Enforce the browser egress boundary on supported Windows launches (Disabled State Only)

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Url/Browser/IBrowserProcessSandbox.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Url/Browser/WindowsBrowserProcessSandbox.cs`
- Modify: `src/InvoiceFlowAI.UrlRecovery.Worker/WorkerHost.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/WindowsBrowserProcessSandboxTests.cs`
- Verify: `build/windows/publish_dotnet_app.ps1`

- [ ] **Step 1: Add a launch test that verifies browser direct egress is denied while proxy loopback access is permitted**

Run the test in the same Windows CI image used for the packaged worker. The test must attempt an external direct connection from the spawned browser process and assert the attempt is blocked; a Playwright request callback alone does not satisfy the test.

- [ ] **Step 2: Run the sandbox test and treat unsupported host enforcement as a no-go**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~WindowsBrowserProcessSandboxTests`

Expected: FAIL until the worker browser process is restricted; if the CI host cannot enforce the rule, leave Browser strategy DI disabled and record the platform as unsupported.

- [x] **Step 3: Implement the supported Windows sandbox or keep browser registration disabled**

```csharp
if (!_sandbox.IsEnforced)
    throw new UrlRecoveryException("URL_BROWSER_SANDBOX_UNAVAILABLE", "Browser recovery is unavailable on this system.", false, false);
```

The worker must not silently launch a browser without confirmed sandbox enforcement. Surface only the safe disabled reason code.

- [ ] **Step 4: Run sandbox, browser strategy, worker, and full solution tests**

Run: `dotnet test InvoiceFlowAI.sln`

Expected: PASS; on unsupported hosts the generic browser strategy remains unavailable rather than weakening SSRF controls.

- [ ] **Step 5: Commit the verified platform boundary**

```bash
git add src/InvoiceFlowAI.Infrastructure/Url/Browser/IBrowserProcessSandbox.cs src/InvoiceFlowAI.Infrastructure/Url/Browser/WindowsBrowserProcessSandbox.cs src/InvoiceFlowAI.UrlRecovery.Worker/WorkerHost.cs tests/InvoiceFlowAI.Infrastructure.Tests/Url/WindowsBrowserProcessSandboxTests.cs build/windows/publish_dotnet_app.ps1
git commit -m "security: restrict browser worker network access"
```

## Final Verification

Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj` and `dotnet test InvoiceFlowAI.sln`. Browser recovery may be enabled only if the Windows sandbox test proves direct egress is denied before navigation.
