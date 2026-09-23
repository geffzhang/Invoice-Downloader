# .NET 10 InvoiceFlowAI Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Windows 11 x64 .NET 10 WinUI 3/WebView2 InvoiceFlowAI application described in the approved migration specification, preserving the Python behavior contract while replacing runtime dependencies with C# services, ZeroPipeline, SQLite, DeepSeek, local OCR, and controlled JSON/RPC.

**Architecture:** Use a five-project layered solution: Domain owns immutable business models and deterministic validation; Contracts owns JSON/RPC and persistence-safe DTOs; Application owns orchestration, terminal state, repositories, and services; Infrastructure owns MailKit, EF Core, OCR, PDF/OFD, DeepSeek, Playwright, archive, report, DPAPI, and logging adapters; App owns WinUI 3, WebView2, DI composition, local asset serving, and the RPC bridge. The first executable vertical slice is `bridge.hello -> settings/account bootstrap -> run.start -> deterministic empty mailbox -> run.completed -> report.export -> report.open`.

**Tech Stack:** .NET SDK 10.0.100, `net10.0-windows`, `win-x64`, Windows App SDK 1.8.250916001, WebView2 1.0.4255-prerelease, ZeroPipeline Core/Recipe 1.2.0, EF Core SQLite 10.0.12, MailKit 4.18.0, PDFiumCore 155.0.8057, PdfPig `0.1.17-alpha-202609192350-df33d`, SkiaSharp `4.154.0-preview.1.26454.9`, Sdcb.SimdPaddleOCR 1.4.2, Microsoft.Extensions.AI.OpenAI 10.10.0, Microsoft.Playwright 1.62.0, ClosedXML 0.105.1, Serilog, WiX Toolset 5.0.2.

## Global Constraints

- Target only Windows 11 x64 with `net10.0-windows`, RID `win-x64`, self-contained, unpackaged WinUI 3 application.
- Use SDK `10.0.100`, `rollForward=disable`, `allowPrerelease=false`, and `dotnet restore --locked-mode` in CI.
- Keep Domain free of WebView2, EF Core, HTTP, MailKit, PdfPig, PDFiumCore, SkiaSharp, ZeroPipeline, and vendor SDK references.
- Keep provider secrets out of DTOs, logs, audit events, Recipe, SQLite result JSON, WebView2 events, and fingerprints; use DPAPI-backed `ISecretStore` and logical secret references.
- Use `invoiceflow.rpc.v1`, camelCase JSON, strict unknown-field rejection, stable error envelopes, and no legacy Python method registration in the new dispatcher.
- Use explicit UoW transactions for business state plus audit writes; never expose ambient EF transactions to Application.
- Use typed ZeroPipeline ports; no business payload in `PipelineContext` dictionaries; all input ports use blocking backpressure.
- Preserve deterministic sequence, idempotency, candidate-level failure isolation, terminal priority `Failed > Cancelled > NeedsManualReview > PartialSuccess > Completed`, and the Recipe fixture at `docs/superpowers/fixtures/recipe/invoiceflow.default.v1.json`.
- Do not generate or commit fake native/model/browser hashes. Real release assets and hashes are CI inputs only.

---

### Task 1: Scaffold the solution and build toolchain

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Build.targets`
- Create: `Directory.Packages.props`
- Create: `NuGet.Config`
- Create: `InvoiceFlowAI.sln`
- Create: `src/InvoiceFlowAI.Domain/InvoiceFlowAI.Domain.csproj`
- Create: `src/InvoiceFlowAI.Contracts/InvoiceFlowAI.Contracts.csproj`
- Create: `src/InvoiceFlowAI.Application/InvoiceFlowAI.Application.csproj`
- Create: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.Infrastructure.csproj`
- Create: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`
- Create: `src/InvoiceFlowAI.Installer/InvoiceFlowAI.Installer.wixproj`
- Test: `tests/*/*.csproj`

**Interfaces:**
- Produces the project reference graph from the specification and a deterministic Release build target.
- `Domain <- Contracts <- Application <- Infrastructure`; `App` references Application/Infrastructure/WebView2/Windows App SDK; Installer references publish output only.

- [ ] **Step 1: Write the failing toolchain check**

Create `build/verify-toolchain.ps1` that exits nonzero unless `.NET SDK 10.0.100`, Windows SDK `10.0.26100.1`, MSVC `14.44.35207`, and WiX `5.0.2` are present.

- [ ] **Step 2: Run the check and verify it fails before scaffold**

Run: `pwsh -File build/verify-toolchain.ps1`
Expected before installation/scaffold: a nonzero result naming the first missing tool or version.

- [ ] **Step 3: Create the solution and projects**

Use the exact project graph above. Enable nullable, implicit usings, deterministic builds, warnings-as-errors, lock files, and `net10.0-windows` only where Windows APIs are required.

- [ ] **Step 4: Restore and compile the empty solution**

Run: `dotnet restore --locked-mode`
Expected: restore succeeds with committed lock files.

Run: `dotnet build -c Release --no-restore`
Expected: all projects compile with zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config InvoiceFlowAI.sln src tests build/verify-toolchain.ps1
git commit -m "build: scaffold dotnet solution and toolchain"
```

### Task 2: Port Contracts, Domain models, and golden fixture loading

**Files:**
- Create: `src/InvoiceFlowAI.Domain/Invoices/*.cs`
- Create: `src/InvoiceFlowAI.Domain/Candidates/*.cs`
- Create: `src/InvoiceFlowAI.Domain/Rules/*.cs`
- Create: `src/InvoiceFlowAI.Domain/Runs/*.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/*.cs`
- Create: `src/InvoiceFlowAI.Contracts/Serialization/InvoiceJsonContext.cs`
- Create: `tests/InvoiceFlowAI.Domain.Tests/FixtureLoader.cs`
- Create: `tests/InvoiceFlowAI.Contracts.Tests/ContractRoundTripTests.cs`
- Read: `docs/superpowers/fixtures/**/*.json`

**Interfaces:**
- Produces `DocumentIdentity`, `DocumentCandidate`, `InvoiceDocument`, `CandidateProcessResult`, `RunInput`, `RunSummary`, RPC envelopes, `RpcError`, account/ruleset/report DTOs, and strict JSON serializer options.
- Contracts must round-trip all committed fixtures with camelCase, enum strings, DateOnly `yyyy-MM-dd`, and decimal invariant formatting.

- [ ] **Step 1: Add failing fixture tests**

Tests must load Recipe, RuleSet, provider/parser registries, account RPC, progress event, provider conflict, parser conflict, account test, URL errors, release manifest, and email-body receipt fixtures. Assert expected schema/version and key counts.

- [ ] **Step 2: Run tests and verify missing types fail**

Run: `dotnet test tests/InvoiceFlowAI.Contracts.Tests -c Release`
Expected: compile failures for missing DTOs/serializer.

- [ ] **Step 3: Implement immutable Domain and Contracts records**

Use the exact names and fields from the design specification. Reject unknown JSON fields with `JsonUnmappedMemberHandling.Disallow`; validate IDs, decimal finiteness, confidence range, identity preservation, and terminal invariants before Application receives a DTO.

- [ ] **Step 4: Run round-trip tests**

Run: `dotnet test tests/InvoiceFlowAI.Contracts.Tests -c Release`
Expected: all fixture round-trip tests pass and no secret-like field appears in serialized non-secret DTOs.

- [ ] **Step 5: Commit**

```powershell
git add src/InvoiceFlowAI.Domain src/InvoiceFlowAI.Contracts tests/InvoiceFlowAI.Contracts.Tests tests/InvoiceFlowAI.Domain.Tests
git commit -m "feat: add domain and rpc contracts"
```

### Task 3: Implement canonical JSON, Recipe registry, and RuleSet bootstrap

**Files:**
- Create: `src/InvoiceFlowAI.Application/Configuration/ICanonicalJsonSerializer.cs`
- Create: `src/InvoiceFlowAI.Application/Configuration/RecipeRegistry.cs`
- Create: `src/InvoiceFlowAI.Application/Configuration/RuleSetBootstrapper.cs`
- Create: `src/InvoiceFlowAI.Application/Rules/RuleSetValidator.cs`
- Create: `src/InvoiceFlowAI.Application/Rules/ProviderRuleRegistry.cs`
- Create: `src/InvoiceFlowAI.Application/Rules/SpecialParserRegistry.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Configuration/*`

**Interfaces:**
- `RecipeRegistry.LoadDefaultAsync()` returns validated `PipelineRecipe`.
- `RuleSetBootstrapper.EnsureDefaultAsync(IUnitOfWork, CancellationToken)` idempotently inserts `default` version `1`, verifies fingerprint when present, and initializes `UserSettings` to `RuleSetVersion=1`.
- `ConfigurationFingerprintService.ComputeAsync(...)` returns lowercase SHA-256 fingerprints.

- [ ] **Step 1: Write failing tests**

Cover unknown Recipe ports, the corrected typed graph, duplicate default RuleSet bootstrap, fingerprint mismatch, provider/parser registry ordering, and canonical JSON stability under object-property reordering.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests --filter FullyQualifiedName~Configuration -c Release`
Expected: failures because registry/bootstrap services do not exist.

- [ ] **Step 3: Implement registries and bootstrap**

Load `invoiceflow.default.v1.json`, validate every port against the registry, load `default.v1.json`, insert it transactionally after migrations, and never overwrite an existing version. Use the fixed `recover.Candidates -> extract.Candidates -> pair.Results -> archive.Pairs -> report.Archived` graph.

- [ ] **Step 4: Re-run tests**

Expected: schema, graph, bootstrap idempotency, provider/parser ordering, and fingerprint tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/InvoiceFlowAI.Application tests/InvoiceFlowAI.Application.Tests
git commit -m "feat: add recipe ruleset and fingerprint registries"
```

### Task 4: Implement EF Core schema, repositories, legacy importer, and DPAPI

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/*.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/20260923_InitialSchema.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/InvoiceFlowDbContextModelSnapshot.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Repositories/*.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Security/DpapiSecretStore.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Security/LegacySettingsImporter.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/*`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Security/*`

**Interfaces:**
- Implements `IRunRepository`, `IDocumentRepository`, `IInvoiceRepository`, `IManualReviewRepository`, `IArchiveRepository`, `IRuleSetStore`, `IMailboxAccountStore`, `IRunStateStore`, `IAuditStore`, `IEventReplayStore`, and `ISecretStore`.
- `LegacySettingsImporter` migrates non-secret fields only, records `LegacyImportState`, requires secret re-entry, and atomically removes the old source after successful re-entry.

- [ ] **Step 1: Write failing SQLite tests**

Test empty migration, `UserSettings` foreign key to `RuleSets`, `MailboxAccounts` revision conflict, RuleSet bootstrap, append-only audit, RunEvents sequence uniqueness, and legacy secret re-entry without old-secret decryption.

- [ ] **Step 2: Run tests before implementation**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Persistence|FullyQualifiedName~Security -c Release`
Expected: failures for missing DbContext/migration/services.

- [ ] **Step 3: Implement migration and entities**

Create `20260923_InitialSchema` with all tables, foreign keys, partial indexes, `UserSettings` singleton, `RuleSets`, `MailboxAccounts`, RunEvents, and audit constraints. Run migrations only from startup recovery.

- [ ] **Step 4: Implement DPAPI and legacy import**

Use `CurrentUser` plus application-derived entropy. Detect old protected fields without decrypting them, write `LEGACY_SECRET_REQUIRES_REENTRY`, map non-secret fields, and block fallback reads after cleanup failure.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests -c Release`
Expected: migration, transaction, concurrency, DPAPI, importer, and append-only tests pass on Windows.

- [ ] **Step 6: Commit**

```powershell
git add src/InvoiceFlowAI.Infrastructure tests/InvoiceFlowAI.Infrastructure.Tests
git commit -m "feat: add sqlite persistence dpapi and legacy importer"
```

### Task 5: Implement terminal state, candidate isolation, checkpoints, and event replay

**Files:**
- Create: `src/InvoiceFlowAI.Application/Runs/RunCoordinator.cs`
- Create: `src/InvoiceFlowAI.Application/Runs/TerminalDecisionService.cs`
- Create: `src/InvoiceFlowAI.Application/Runs/CheckpointService.cs`
- Create: `src/InvoiceFlowAI.Application/Runs/EventReplayService.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Runs/*`

**Interfaces:**
- Produces `RunTerminalDecision` with priority `Failed > Cancelled > NeedsManualReview > PartialSuccess > Completed`.
- Candidate failures remain `CandidateProcessResult` values; only persistence/audit/report/graph/authentication failures become `RunFailure`.
- Each committed packet writes business state, audit, checkpoint, RunEvent, and `Runs.LastEventSequence` in one UoW.

- [ ] **Step 1: Write failing tests**

Cover all five terminal statuses, candidate `Unresolved/Timeout/QuotaExhausted`, cancellation-vs-failure ordering, non-critical finalizer failures, event gaps, snapshot refresh, duplicate packet replay, and crash points before/after transaction commit.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests --filter FullyQualifiedName~Runs -c Release`
Expected: failures for terminal/replay services.

- [ ] **Step 3: Implement services**

Do not infer success from “no exception”. Compute terminal status only after the final barrier, persist the decision once, and return existing state for duplicate completion.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests --filter FullyQualifiedName~Runs -c Release
git add src/InvoiceFlowAI.Application/Runs tests/InvoiceFlowAI.Application.Tests/Runs
git commit -m "feat: add terminal state checkpoint and event replay"
```

### Task 6: Implement archive two-phase commit and recovery

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Archive/ArchiveCommitCoordinator.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Archive/ArchiveRecoveryService.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/*`

**Interfaces:**
- Implements `IArchiveCommitCoordinator` with idempotency key `RunId + DocumentId + ProcessingRevision + Role + ContentHash`.
- Uses `Absent -> Prepared -> Committed`, with `RecoveryRequired` only for ambiguous evidence.

- [ ] **Step 1: Write failing recovery tests**

Cover DB Prepared + temp only, Prepared + final same hash, Prepared + final different hash, missing both files, orphan final without DB record, repeated prepare/move/commit, and process termination at each phase.

- [ ] **Step 2: Run tests before implementation**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Archive -c Release`
Expected: failures for coordinator/recovery services.

- [ ] **Step 3: Implement transactional coordinator**

Prepare temp file and hash; commit Prepared and audit in transaction A; atomically move and fsync; validate final hash; commit Committed and audit in transaction B. Recovery must never silently delete ambiguous files.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Archive -c Release
git add src/InvoiceFlowAI.Infrastructure/Archive tests/InvoiceFlowAI.Infrastructure.Tests/Archive
git commit -m "feat: add durable archive commit recovery"
```

### Task 7: Implement report export/open and replace path APIs

**Files:**
- Create: `src/InvoiceFlowAI.Application/Reports/ReportApplicationService.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Reports/ClosedXmlReportExporter.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/ReportRpcHandlers.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Reports/*`
- Create: `tests/InvoiceFlowAI.App.Tests/Rpc/ReportRpcTests.cs`

**Interfaces:**
- `report.export` accepts `ReportExportRequest` and returns `ReportExportRpcResult`.
- `report.open` returns one-time `ReportOpenToken` bound to run, relative path, expiry, and content hash.
- Legacy absolute-path methods are not registered.

- [ ] **Step 1: Write failing tests**

Test fixed three-sheet schema, failed candidates included, numeric/date/null formatting, duplicate export content hash, token expiry, token replay, hash mismatch, and absolute-path rejection.

- [ ] **Step 2: Implement exporter and handlers**

Use ClosedXML 0.105.1, persist relative report path/hash, enforce report token lifetime, and map failures to `REPORT_EXPORT_FAILED` field details.

- [ ] **Step 3: Run tests and commit**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Reports -c Release
dotnet test tests/InvoiceFlowAI.App.Tests --filter FullyQualifiedName~ReportRpc -c Release
git add src/InvoiceFlowAI.Application/Reports src/InvoiceFlowAI.Infrastructure/Reports src/InvoiceFlowAI.App/Rpc/ReportRpcHandlers.cs tests

git commit -m "feat: add report export and secure open tokens"
```

### Task 8: Implement DeepSeek/OCR and email-body receipt pipeline

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Ai/DeepSeekAdapter.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Ocr/SimdPaddleOcrService.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Documents/EmailBodyReceiptParsers/*.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Ai/*`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Documents/EmailBodyReceiptTests.cs`

**Interfaces:**
- Implements `IDeepSeekAdapter`, `IInvoiceFieldExtractor`, `IInvoiceOcr`, `IEmailBodyReceiptParserRegistry`.
- Provider order: Baiwang 400, Fpyun 390, 51fapiao 380, iCloud 370.
- All receipt results pass normalizer and acceptance service and preserve candidate identity.

- [ ] **Step 1: Write failing tests**

Load four parser success fixtures, missing-field fixtures, conflict fixture, fake `IChatClient` text/vision requests, 401/429/timeout/image-size cases, and `InputKind=EmailBodyReceipt` trace assertions.

- [ ] **Step 2: Implement adapters**

Use only `IChatClient`/DeepSeek in Infrastructure; keep raw text/images out of logs and persistence; reject GLM configuration and require DeepSeek secret re-entry.

- [ ] **Step 3: Run tests and commit**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Ai|FullyQualifiedName~EmailBody -c Release
git add src/InvoiceFlowAI.Infrastructure/Ai src/InvoiceFlowAI.Infrastructure/Ocr src/InvoiceFlowAI.Infrastructure/Documents tests

git commit -m "feat: add deepseek ocr and email body extraction"
```

### Task 9: Implement WebView2 host, JSON/RPC client, and migrate existing frontend

**Files:**
- Create: `src/InvoiceFlowAI.App/WebView/WebView2Host.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/WebViewRpcBridge.cs`
- Create: `src/InvoiceFlowAI.App/AppHost.cs`
- Modify: `templates/index_app.js`
- Modify: `templates/index.html`
- Create: `tests/InvoiceFlowAI.App.Tests/WebView/*`
- Create: `tests/InvoiceFlowAI.App.Tests/Rpc/*`

**Interfaces:**
- WebView2 host uses `https://app.local/Web/...`, `IWebViewAssetVerifier`, `IWebViewNavigationPolicy`, and `IRpcDispatcher`.
- Frontend uses `RpcClient`, one `AppStore` reducer, `bridge.hello`, event sequence checks, `run.get` replay, `account.*`, `ruleset.*`, `report.export/open`, and no `window.pywebview.api`.

- [ ] **Step 1: Write failing bridge/frontend tests**

Test handshake, unknown legacy method rejection, request timeout/cancel, event gap replay, page reload, old page message rejection, account settings, report export/open, and no secret in sessionStorage.

- [ ] **Step 2: Implement host and bridge**

Follow the fixed initialization sequence and execute RPC handlers outside the UI thread, posting responses/events back on the WebView2 UI thread.

- [ ] **Step 3: Migrate the three existing pages**

Replace old polling and direct path APIs with reducer actions and selectors. Preserve visible workflow and copy while replacing GLM controls with DeepSeek/secret status.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests/InvoiceFlowAI.App.Tests -c Release
node --check templates/index_app.js
git add src/InvoiceFlowAI.App templates tests/InvoiceFlowAI.App.Tests
git commit -m "feat: migrate frontend to webview rpc"
```

### Task 10: Implement deterministic mailbox, parser, pairing, and archive integration

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Mail/MailKitMailboxScanner.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Documents/*Parser.cs`
- Create: `src/InvoiceFlowAI.Application/Pipeline/Nodes/*.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Mail/*`
- Create: `tests/InvoiceFlowAI.Application.Tests/Pipeline/*`

**Interfaces:**
- Implements MailKit scanning, UID cursor, ZIP limits, provider registry, parser registry, URL recovery, extraction, pairing, archive, and report nodes using the typed Recipe graph.
- Every candidate emits exactly one terminal result; all node failures follow the run/candidate boundary.

- [ ] **Step 1: Write failing behavior tests**

Port existing Python samples and tests for date boundaries, UIDVALIDITY, provider/direct URL families, OFD/XML/PDF priority, special parser conflicts, pairing, dedupe, archive naming, and candidate failure isolation.

- [ ] **Step 2: Implement nodes and adapters**

Use application DTOs only at node ports; use infrastructure adapters behind interfaces; preserve sequence/reorder/retry behavior.

- [ ] **Step 3: Run tests and commit**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests tests/InvoiceFlowAI.Application.Tests -c Release
git add src tests

git commit -m "feat: implement invoice processing pipeline"
```

### Task 11: Build manifests, fixed assets, WiX installer, and CI

**Files:**
- Create: `build/model-manifest.ps1`
- Create: `build/release-manifest.ps1`
- Create: `.github/workflows/windows.yml`
- Create: `src/InvoiceFlowAI.Installer/Product.wxs`
- Create: `licenses/THIRD-PARTY-NOTICES.txt`
- Modify: `Directory.Build.targets`
- Test: `tests/InvoiceFlowAI.App.Tests/Release/*`

**Interfaces:**
- Produces publish directory, model/browser/release manifests, signed x64 MSI, license inventory, and asset validation results.

- [ ] **Step 1: Write failing manifest tests**

Test missing asset, wrong length/hash, wrong PE architecture, missing Fixed Runtime, Chromium revision mismatch, model manifest mismatch, unsigned production asset, and path traversal.

- [ ] **Step 2: Implement scripts and WiX packaging**

Never download assets during WiX build; consume only locked, hashed publish inputs. Do not commit generated real hashes to source fixtures.

- [ ] **Step 3: Run publish and installer validation**

```powershell
pwsh -File build/verify-toolchain.ps1
dotnet restore --locked-mode
dotnet test -c Release
dotnet publish src/InvoiceFlowAI.App -c Release -r win-x64 --self-contained true
pwsh -File build/model-manifest.ps1
pwsh -File build/release-manifest.ps1
wix build src/InvoiceFlowAI.Installer/Product.wxs -arch x64
```

- [ ] **Step 4: Commit**

```powershell
git add build .github src/InvoiceFlowAI.Installer licenses Directory.Build.targets tests

git commit -m "build: add release manifests installer and ci"
```

### Task 12: Windows end-to-end acceptance and migration audit

**Files:**
- Create: `tests/InvoiceFlowAI.App.Tests/EndToEnd/WindowsReleaseTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/*`
- Create: `docs/superpowers/fixtures/migration/*`
- Modify: `docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md` only for verified deviations

**Interfaces:**
- Produces evidence for clean Windows 11 x64 startup, WebView2 local load, DPAPI, account revision, empty mailbox, complete run, cancellation, failure isolation, report export/open, archive recovery, OCR asset loading, DeepSeek fake/compatibility behavior, and uninstall/upgrade preservation.

- [ ] **Step 1: Build the full fixture matrix**

Include legacy settings with secret fields, each of the five old terminal states, report export/open replay, archive crash points, all four email-body providers, all four special parsers, and old absolute path rejection.

- [ ] **Step 2: Run deterministic integration suite**

Run: `dotnet test -c Release --collect:"XPlat Code Coverage"`
Expected: all contracts, migration, persistence, pipeline, RPC, WebView2 fake-host, report, archive, and parser tests pass.

- [ ] **Step 3: Run clean Windows acceptance**

Install the signed MSI on a clean Windows 11 x64 machine, run startup/asset verification, execute an empty-mailbox run, and verify no Python process or network model download is required.

- [ ] **Step 4: Record remaining external validation only**

Keep DeepSeek live endpoint verification, OCR quality measurement, real provider browser flows, and final release asset hashes as external release checks; do not claim them from fake tests.

- [ ] **Step 5: Commit**

```powershell
git add tests docs/superpowers/fixtures docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md
git commit -m "test: verify migration behavior and release acceptance"
```

## Execution Order

Tasks 1-3 establish the solution and contract foundation. Tasks 4-6 establish durable state, terminal semantics, and archive recovery. Task 7 provides the first user-visible reporting path. Task 8 provides extraction and email-body behavior. Task 9 connects the actual frontend. Task 10 completes the business pipeline. Tasks 11-12 package and verify the release.

The first useful vertical slice is Tasks 1-7 with a fake empty mailbox and fake DeepSeek client. Do not wait for real OCR models, Playwright assets, or live DeepSeek access to validate RPC, SQLite, report, terminal-state, and recovery behavior.

## Plan Self-Review

- **Spec coverage:** project scaffold, typed Recipe DAG, settings/accounts/secrets, RuleSets, SQLite/UoW/audit/events/checkpoints, five terminal states, candidate isolation, MailKit, URL providers, parsers, OCR, DeepSeek, report export/open, WebView2, frontend migration, manifests, WiX, CI, and Windows acceptance all have tasks.
- **Typed consistency:** `RecoverUrls` uses `Candidates<PipelineItem<CandidateBatch>>`; `ExtractDocuments` consumes Candidates and emits `Results<PipelineItem<ExtractionBatch>>`; pairing emits Pairs; archive emits Archived; report consumes Archived. This matches the corrected Recipe fixture.
- **No fake assets:** model, browser, native library, and release hashes remain CI-generated and are not fabricated in the plan.
- **Known external checks:** live DeepSeek compatibility, OCR accuracy, real provider browser flows, code signing, and final asset hashes remain explicit release checks rather than claimed local test results.
