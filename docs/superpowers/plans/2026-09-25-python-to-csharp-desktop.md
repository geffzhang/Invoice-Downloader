# Desktop Python-to-C# Replacement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Python as the InvoiceFlowAI desktop runtime and business engine with a Windows x64 Avalonia/C# application, then retire the old pywebview desktop runtime and remove the DSH plugin.

**Architecture:** Keep `invoiceflow.rpc.v1` as the Web UI boundary and make the Avalonia host own window/file actions and typed run RPC. Reuse the existing C# mailbox, candidate, recovery, extraction, pairing, archive, lifecycle, and ClosedXML components; fill the verified integration gaps in run admission/execution, report data loading/stage registration, results projection, and RPC composition. Migrate and validate the UI before deleting the old Python desktop and packaging paths.

**Tech Stack:** .NET SDK 10.0.100, Avalonia 12.1.0, Avalonia.Controls.WebView 12.1.0, EF Core SQLite 10.0.12, MailKit, ZeroPipeline, ClosedXML, xUnit/FluentAssertions, Node.js built-in test runner, PowerShell release scripts.

## Global Constraints

- Use RPC protocol `invoiceflow.rpc.v1`, camelCase JSON, strict DTOs, and stable error codes.
- The finished Windows x64 desktop runtime and release must not require, launch, or bundle Python.
- Keep secrets out of run DTOs, ordinary events/results, logs, and browser storage; resolve configured credentials inside C# services.
- Preserve existing C# settings/account/DPAPI/session-secret behavior.
- Use the C# workbook structure `Summary`, `Invoices`, and `Manual Reviews`; matching the legacy Python workbook is not required.
- Remove `plugins/dsh-invoice-downloader` directly; do not port it.
- Stop producing or advertising macOS desktop releases; Windows x64 is the only desktop target after retirement.
- Keep the old Python desktop only as a temporary comparison/fallback until the C# workflow passes acceptance; never run it as a sidecar of the C# app.
- Retire Python modules/tests only when they belong to the desktop engine or are required solely by the removed DSH plugin. Retain independent developer tools only if they are not runtime/release dependencies.
- Do not commit changes unless the user separately requests it.
- Follow TDD. Each task ends with its focused test/verification command before moving to the next task.

## Current Checkpoint (2026-09-25)

- Tasks 1, 2, and 4–7 are implemented and their focused checks have passed.
- Task 3 execution orchestration, event publishing, service registration, and execution tests are implemented. Provider/rule parity fixtures and production progress/log projection remain open; do not treat pipeline execution as parity evidence.
- Task 8 NativeWebView run acceptance is complete. The opt-in Windows E2E completes a fake empty-mailbox run through the real bridge, results, workbook export, and host open path; it checks the three-sheet schema and workbook counts against the persisted results snapshot. No real mailbox or provider is contacted.
- Task 9 retirement is implemented: Windows releases use the C#/.NET publish tree and Python-free audit; DSH and macOS/PyInstaller release surfaces are removed. Python reference modules remain only where retained tests, truth-data builders, or audit tools consume them.

---

## File Map

**RPC contracts:** `src/InvoiceFlowAI.Contracts/Rpc/`, `Runs/`, `Reports/`, and contract round-trip tests define the wire surface. Existing `RunProgressPayload` is intentionally small and must be extended or supplemented to represent the UI's actual snapshot needs.

**Run and pipeline services:** `src/InvoiceFlowAI.Application/Runs/`, `Pipeline/`, `Persistence/`, and `src/InvoiceFlowAI.Domain/Runs/` contain lifecycle primitives and recipe execution abstractions. `PipelineRunFactory` and `PipelineRecipeGraphFactory` already create a scoped recipe graph; a desktop run service still needs to own admission, active-run cancellation, orchestration, and event/result projection.

**Infrastructure and persistence:** `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAIInfrastructureServiceCollectionExtensions.cs` registers concrete processing stages. It currently does not register an `IReportRunDataSource` implementation or an `IReportExportStage` implementation. Existing run/document/invoice/pairing/archive rows and stores provide the read/write boundary to extend.

**Desktop composition:** `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`, `App.axaml.cs`, `MainWindow.axaml.cs`, `Rpc/AppRpcComposition.cs`, and `Rpc/WebViewRpcBridge.cs` own DI, the native bridge, and handler registration. Existing settings handlers use `TypedRpcHandler` and `ScopedRpcHandler<THandler>` patterns.

**Frontend:** `templates/index_app.js`, `templates/rpc_client.js`, and `templates/bridge-bootstrap.js` contain the remaining legacy API call sites. Existing Node tests in `tests/InvoiceFlowAI.App.Tests/Rpc/` assert settings migration and can be extended with run/results/file behavior coverage.

**Acceptance and retirement:** `tests/InvoiceFlowAI.App.Tests/EndToEnd/`, `build/windows/publish_dotnet_app.ps1`, `.github/workflows/windows.yml`, and README release sections cover shipping. Retirement searches must include all repository references to pywebview/PyInstaller/macOS desktop assets, not only these known files.

---

### Task 1: Freeze the Remaining Desktop Contract and Define Typed DTOs

**Files:**

- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunContextSnapshot.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunStartRequest.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunStartResult.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunProgressSnapshot.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunProgressStats.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunLogEntry.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunStopRequest.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunResultsSnapshot.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/DesktopFileActionRequest.cs`
- Create: `src/InvoiceFlowAI.Contracts/Rpc/DesktopActionResult.cs`
- Test: `tests/InvoiceFlowAI.Contracts.Tests/RunRpcContractTests.cs`
- Test: `tests/InvoiceFlowAI.Contracts.Tests/ContractRoundTripTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs`

**Interfaces:**

- `RunStartRequest` contains `RunId`, `AccountId`, `DateFrom`, `DateTo`, `OutputDirectory`, `CompanyName`, and `RunMode`; it contains no email password, authorization code, API key, or secret name/value.
- `RunProgressSnapshot` contains `RunState`, `IsRunning`, `CanStop`, `StopRequested`, `Progress`, `StatusText`, `Stats`, `Logs`, `LastError`, `QuotaExhausted`, `QuotaMessage`, `BuildIdentity`, `RawDateRange`, and `ImapQueryRange`. `Stats` contains `Emails`, `Invoices`, and `Errors`; log entries contain only timestamp, level, and sanitized display text. `Logs` is bounded to the latest 20 entries.
- `RunResultsSnapshot` contains the current UI data: `Categories`, `SuccessInvoices`, `ErrorInvoices`, `GroupedErrorInvoices`, `ManualCheckPath`, `OutputPath`, `Summary`, `BuildIdentity`, `RawDateRange`, `ImapQueryRange`, `ResultBreakdown`, `ReasonCodeBreakdown`, quota state/message, and `LastExportPath`. Do not preserve the redundant `invoices` alias unless a tested in-repo consumer requires it.
- `run.progress` remains a pushed event; `RunProgressSnapshot` is also returned by the snapshot query so refresh/reconnect does not depend on missed events.
- Freeze the remaining frontend operations as `run.context.get`, `run.start`, `run.progress.get`, `run.stop`, `run.results.get`, `run.report.export`, `run.folder.open`, `run.manual-review.open`, `run.file.open`, `window.minimize`, `window.maximize`, and `window.close`. Do not register legacy pywebview method names.

- [x] **Step 1: Add failing strict round-trip tests** for all new DTOs, enum/status serialization, camelCase property names, null/empty behavior, and the invariant that serialized start/progress/results payloads contain no secret fields or fixture secret values.
- [x] **Step 2: Run the Contracts tests** with `dotnet test tests/InvoiceFlowAI.Contracts.Tests/InvoiceFlowAI.Contracts.Tests.csproj --no-restore`; confirm the missing DTO tests fail to compile before implementation.
- [x] **Step 3: Add the DTO records** using existing serializer conventions in `InvoiceFlowAI.Contracts`; keep date/time types explicit and avoid frontend-specific `JsonElement` fields.
- [x] **Step 4: Re-run the Contracts project** and assert JSON fixtures deserialize and serialize with the exact v1 camelCase contract.
- [x] **Step 5: Add a failing UI contract assertion** that lists every remaining legacy operation from `templates/index_app.js`; run `node --test tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs` and record the current legacy surface as the expected red baseline.

### Task 2: Implement Run Admission, Active-Run Ownership, and Cancellation

**Files:**

- Create: `src/InvoiceFlowAI.Application/Runs/IDesktopRunService.cs`
- Create: `src/InvoiceFlowAI.Application/Runs/DesktopRunService.cs`
- Create: `src/InvoiceFlowAI.Application/Runs/ActiveRunRegistry.cs`
- Modify: `src/InvoiceFlowAI.Application/Persistence/IRunLifecycleStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfRunLifecycleStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/RunRow.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Runs/DesktopRunServiceTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/EfRunLifecycleStoreTests.cs`

**Interfaces:**

- `IDesktopRunService.GetContextAsync(CancellationToken)` returns the current account/output/date context without secrets.
- `IDesktopRunService.StartAsync(RunInput, CancellationToken)` validates and reserves exactly one active run, then returns promptly with an accepted/rejected result; it does not execute the mailbox workflow on the RPC request thread.
- `IDesktopRunService.GetProgressAsync(string? runId, CancellationToken)` returns the stable progress snapshot for the active or requested run.
- `IDesktopRunService.StopAsync(string runId, CancellationToken)` durably marks cancellation requested and cancels the in-process run token; repeated stop requests are idempotent.
- The active registry owns one cancellation source, run task, and run id at a time. It releases ownership only after pipeline disposal and all finalizers complete.
- Extend lifecycle persistence with explicit run creation/reservation and stage/progress update operations. The service must enforce `RUN_ALREADY_ACTIVE`, `RUN_NOT_FOUND`, and `RUN_NOT_CANCELLABLE` using existing error codes where applicable.

- [x] **Step 1: Add failing Application tests** for successful reservation, a second concurrent start rejection, validation failure before side effects, cancellation request idempotency, not-found/non-cancellable stop, and ownership held until finalization completes.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~DesktopRunServiceTests` and confirm the missing service/behavior fails.
- [x] **Step 3: Add the lifecycle-store operations** and failing SQLite tests for Created → Running → terminal transitions, cancellation timestamp persistence, and one-active-run admission.
- [x] **Step 4: Implement the EF lifecycle operations** using the existing unit-of-work and run row; do not store credentials or raw request JSON in the run row.
- [x] **Step 5: Implement `ActiveRunRegistry` and `DesktopRunService`** with one controlled background task and cancellation source; resolve credentials through existing account/secret services only inside the run scope.
- [x] **Step 6: Run** `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~DesktopRunServiceTests` and `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~EfRunLifecycleStoreTests`.

### Task 3: Execute the Existing C# Pipeline and Publish Durable Run Events

**Files:**

- Create: `src/InvoiceFlowAI.Application/Runs/RunExecutionService.cs`
- Create: `src/InvoiceFlowAI.Application/Runs/IRunEventPublisher.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/WebViewRunEventPublisher.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAIInfrastructureServiceCollectionExtensions.cs`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `src/InvoiceFlowAI.App/MainWindow.axaml.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/WebViewRpcBridge.cs` only for a tested event-sink integration gap
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/DesktopParityFixtureTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity/` synthetic JSON fixtures
- Test: `tests/InvoiceFlowAI.Application.Tests/Runs/RunExecutionServiceTests.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pipeline/PipelineRecipeGraphFactoryTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/WebViewRpcBridgeTests.cs`

**Interfaces:**

- `RunExecutionService` takes the admitted `RunInput`, creates a `PipelineRun` from the validated C# recipe, submits the input, starts the graph, collects stage output/failure/completion, and calls `IRunCoordinator.FinalizeAsync` exactly once after the final barrier.
- `IRunEventPublisher.Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)` is an application-to-host event boundary. The WebView bridge remains the only class that serializes/posts an RPC event envelope.
- Use the existing stage implementations: `MailKitMailboxScanner`, `CandidateCollectionStage`, `UrlRecoveryStage`, `DocumentExtractionStage`, `ArtifactPairingStage`, and `DocumentArchivingStage`. Do not add alternate processing implementations in this task.
- Build a Python-to-C# parity matrix from `tests/test_mailbox_scanner.py`, `tests/test_provider_url_recovery.py`, `tests/test_processing_pipeline.py`, `tests/test_pairing_engine.py`, and `tests/test_task8_review_remediation.py`. Store only synthetic, sanitized expected outputs; do not retain credentials, real email contents, or signed URL query strings.
- Cover each provider/rule family used by the desktop Python engine; record whether C# already matches, needs a port, or is intentionally unsupported. A pipeline pass is not parity evidence by itself.
- Persist each candidate result and event/checkpoint through existing stores/UoW before publishing its event. If a commit fails, stop downstream side effects and finalize as failed with a safe stable reason code.
- Progress calculations must preserve the fields used by `ProcessingPage`: running/terminal state, percent, status text, email/invoice/error counts, bounded logs, last safe error, quota status, and stop-request status.

- [x] **Step 1: Inventory desktop Python provider/rule behavior** from the named Python tests and runtime imports; produce a matrix whose every row has a C# implementation/test or an explicit unsupported/retired decision.
- [x] **Step 2: Add sanitized golden fixtures and failing C# parity tests** for mailbox candidate order, representative URL provider recovery, extraction fields, pairing decisions, archive statuses, and safe failure reasons.
- [x] **Step 3: Run the focused parity tests** with `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~DesktopParityFixtureTests`; verify uncovered cases fail before porting.
- [x] **Step 4: Add failing execution tests** with a recipe using fake stage implementations; assert ordered stages, empty-mailbox finalization, per-candidate event sequence, event-after-commit ordering, cancellation, stage failure, and single finalization.
- [x] **Step 5: Run** `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~RunExecutionServiceTests`; verify execution is not wired yet.
- [x] **Step 6: Add the event publisher abstraction and execution orchestration** around `PipelineRunFactory`; keep stage logic in the existing Application/Infrastructure implementations.
- [x] **Step 7: Add a fake publisher bridge test** proving the event envelope has `protocol`, `event`, `runId`, `eventSequence`, `emittedAtUtc`, and `payload`, and that a committed sequence is strictly increasing.
- [x] **Step 8: Register pipeline/run services** with correct singleton/scoped lifetimes; ensure background work owns an async scope until pipeline and finalizers complete.
- [x] **Step 9: Run focused run/pipeline tests** with `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~RunExecutionService|FullyQualifiedName~PipelineRecipeGraphFactory"` and rerun the parity fixture tests.

### Task 4: Add Production Report Data Loading and Pipeline Export Stage

**Files:**

- Create: `src/InvoiceFlowAI.Infrastructure/Reports/EfReportRunDataStore.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Reports/ReportExportStage.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAIInfrastructureServiceCollectionExtensions.cs`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `src/InvoiceFlowAI.Application/Reports/ReportApplicationService.cs`
- Modify: `src/InvoiceFlowAI.Application/Reports/IReportContracts.cs` only if the current report data contract cannot carry the persisted terminal counts/status without lossy reconstruction
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Reports/EfReportRunDataSourceTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Reports/ReportExportStageTests.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Reports/ReportApplicationServiceTests.cs`
- Reuse: `tests/InvoiceFlowAI.Infrastructure.Tests/Reports/ClosedXmlReportExporterTests.cs`

**Interfaces:**

- `EfReportRunDataStore` implements both `IReportRunDataSource` and `IReportPathStore` so export/open operations share one persisted projection and path/hash boundary rather than duplicate EF queries.
- `EfReportRunDataStore.LoadAsync(string runId, CancellationToken)` projects persisted run, invoice, manual-review, and failure-reason records into the existing `ReportRunData` contract. It returns `null` for an unknown/non-reportable run and does not infer missing status counts as zero.
- Persist report path/hash using existing run columns if present; otherwise add a migration and update the EF model snapshot.
- `ReportExportStage.ExecuteAsync(PipelineItem<ArchiveBatch>, CancellationToken)` projects the completed archive results and stored terminal summary to `ReportExportWorkItem`, calls `IReportExporter`, persists relative path/content hash, and returns a `RunSummary` with report metadata.
- `ReportApplicationService` remains the application service for user-triggered export/open operations; do not duplicate workbook rendering there.
- The report path is always relative to the configured output root. Absolute or traversal paths are rejected at the host boundary.
- Keep `ClosedXmlReportExporter`'s accepted three-sheet structure and deterministic content identity. Correct summary counts from persisted run data rather than hardcoding zeros for duplicate/retained/unresolved/cancelled/quota/auth/timeout counts.

- [x] **Step 1: Add failing SQLite projection tests** for a completed run with resolved, duplicate, retained, manual-review, and failed candidates; assert invoice rows, review rows, all persisted counts, and unknown run behavior.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~EfReportRunDataStoreTests` and confirm the production source is missing.
- [x] **Step 3: Implement `EfReportRunDataStore`** using existing `RunRow`, `InvoiceRow`, `ManualReviewItemRow`, `DocumentProcessingRow`, and failure/audit stores; use no ad hoc JSON parsing if typed persisted columns/stores exist.
- [x] **Step 4: Add failing stage tests** for report path/hash persisted only after a successful write, failure mapping, cancellation propagation, and correct counts from the final summary.
- [x] **Step 5: Implement `ReportExportStage` and register `EfReportRunDataStore` for both report interfaces**, plus the exporter, in-memory open-token store, and application service; add a service-resolution test that resolves the report chain without a live database network dependency.
- [x] **Step 6: Run** `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~ReportRunDataSource|FullyQualifiedName~ReportExportStage|FullyQualifiedName~ClosedXmlReportExporter"` and `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~ReportApplicationServiceTests`.

### Task 5: Expose Run, Result, and Report RPCs

**Files:**

- Create: `src/InvoiceFlowAI.App/Rpc/RunContextGetRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/RunStartRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/RunProgressGetRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/RunStopRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/RunResultsGetRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/RunReportExportRpcHandler.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`
- Create: `tests/InvoiceFlowAI.App.Tests/Rpc/RunRpcHandlerTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`
- Test: `tests/InvoiceFlowAI.Contracts.Tests/ContractRoundTripTests.cs`

**Interfaces:**

- Handlers use `TypedRpcHandler<TParams,TResult>` and `ScopedRpcHandler<THandler>`; no handler retains a `DbContext` or window control between requests.
- `run.start` calls `IDesktopRunService.StartAsync` and returns immediately with `RunStartResult`; it must not block until mailbox scanning ends.
- `run.progress.get` accepts an optional run id and returns `RunProgressSnapshot`; `run.results.get` returns `RunResultsSnapshot` or a stable `RUN_NOT_FOUND` result.
- `run.stop` is idempotent and returns whether cancellation was newly requested/already requested; it does not report terminal cancellation before finalization completes.
- `run.report.export` accepts only a run id, not an arbitrary absolute path. It invokes the C# report service and returns the typed report result.
- Advertise all implemented methods in `bridge.hello`; do not advertise a handler before it is registered and resolvable.

- [x] **Step 1: Add failing RPC tests** for each method's success payload, invalid params, unknown run, duplicate start, stop idempotency, missing result, safe error mapping, and secret redaction.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~RunRpcHandlerTests`; confirm handlers/methods are absent.
- [x] **Step 3: Add handlers and scoped registrations**; use existing RPC error codes such as `RUN_ALREADY_ACTIVE`, `RUN_NOT_FOUND`, `RUN_NOT_CANCELLABLE`, `REPORT_EXPORT_FAILED`, and `RPC_INVALID_PARAMS` where their semantics match.
- [x] **Step 4: Extend composition tests** to prove every advertised run method has a registered handler and no old `start_processing`/`get_progress` method is exposed.
- [x] **Step 5: Run** `dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter "FullyQualifiedName~RunRpcHandlerTests|FullyQualifiedName~AppRpcCompositionTests|FullyQualifiedName~AppServiceProviderFactoryTests"`.

### Task 6: Move the Processing and Results UI to Typed RPC

**Files:**

- Modify: `templates/index_app.js`
- Modify: `templates/rpc_client.js` only if the existing client lacks a tested event subscription/snapshot capability
- Modify: `templates/bridge-bootstrap.js` only if event subscription must start during bootstrap
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs`
- Create: `tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/rpc_client.test.mjs`

**Interfaces:**

- Settings page startup obtains run context through `run.context.get`; start sends `run.start` with account id, dates, output directory, company, and mode. Credentials are stored by the existing secret RPC and never re-sent in a run request.
- Processing UI obtains an initial `run.progress.get` snapshot, applies sequenced `run.progress` events, and may poll a snapshot only as reconnect fallback. It preserves current behavior for the one-second UI refresh cadence until event-path tests establish that polling can be removed.
- Preserve fields used by `ProcessingPage`: `runState`, `isRunning`, `canStop`, `stopRequested`, `progress`, `statusText`, `stats.emails`, `stats.invoices`, `stats.errors`, `logs`, `lastError`, `quotaExhausted`, and `quotaMessage`. Terminal completed/failed state continues to navigate to results.
- Results page obtains `run.results.get`, uses typed report export, and displays the returned path/counts. Error grouping and result breakdown must use the same status/reason groupings as the Python baseline fixtures.
- Remove the `load_user_settings` legacy fallback after typed settings bootstrap is verified; use the current `settings.get` snapshot instead.

- [x] **Step 1: Add failing Node tests** for context load, start request shape/no secrets, progress event ordering and merge, reconnect snapshot, stop disabled/idempotency, terminal navigation, result grouping, and report export call.
- [x] **Step 2: Run** `node --test tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs`; confirm tests fail on current legacy `callApi` names.
- [x] **Step 3: Replace the run/progress/result call sites** in `index_app.js` with `RpcClient` calls and event subscription; do not change layout or visible copy as part of this migration.
- [x] **Step 4: Remove all legacy task/settings-fallback/file/export calls** from the page and keep run UI DTO normalization at one typed adapter boundary.
- [x] **Step 5: Run** `node --test tests/InvoiceFlowAI.App.Tests/Rpc/*.test.mjs`, `node --check templates/rpc_client.js`, and `node --check templates/bridge-bootstrap.js`; inspect JSX using the existing Babel compile/test path.

### Task 7: Replace Native Window and File Operations with C# Host Actions

**Files:**

- Create: `src/InvoiceFlowAI.App/Desktop/IDesktopActionService.cs`
- Create: `src/InvoiceFlowAI.App/Desktop/AvaloniaDesktopActionService.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/RunFolderOpenRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/ManualReviewFolderOpenRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/RunFileOpenRpcHandler.cs`
- Create: `src/InvoiceFlowAI.App/Rpc/WindowCommandRpcHandler.cs`
- Modify: `src/InvoiceFlowAI.App/MainWindow.axaml.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `templates/index_app.js`
- Create: `tests/InvoiceFlowAI.App.Tests/Desktop/AvaloniaDesktopActionServiceTests.cs`
- Create: `tests/InvoiceFlowAI.App.Tests/Rpc/DesktopActionRpcHandlerTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs`

**Interfaces:**

- Folder open derives the allowed output/manual-review location from the run/settings service; do not trust an arbitrary UI-provided path.
- File open accepts a persisted document id or report token and resolves it under the configured output root; reject rooted paths, traversal, missing files, and paths outside the allowed root.
- Report export opens through the C# report application/token boundary; do not return a secret or unconstrained absolute path to the UI.
- Window commands dispatch to the active Avalonia window on the UI thread. They do not depend on `pywebview.api` or a Python facade.
- Native actions return stable safe results; raw OS exception paths/details are not sent to JavaScript.

- [x] **Step 1: Add failing tests** for valid output/manual-review folders, missing folder behavior, path traversal/out-of-root file rejection, valid archived-file launch, missing file, UI-thread window dispatch, and safe error messages.
- [x] **Step 2: Run** `dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~DesktopAction`.
- [x] **Step 3: Implement injectable native action services** with fake process/window launchers for tests; use the repository's existing Avalonia dispatcher/window accessor pattern.
- [x] **Step 4: Register the handlers and replace** `open_folder`, `open_manual_check_folder`, `view_invoice`, `minimize_window`, `maximize_window`, and `close_window` call sites.
- [x] **Step 5: Re-run** `dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~DesktopAction` and `node --test tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs`.

### Task 8: Add End-to-End C# Desktop Run Acceptance

**Files:**

- Create: `tests/InvoiceFlowAI.App.Tests/EndToEnd/NativeWebViewRunIntegrationTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/WebViewRpcBridgeTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/EndToEnd/WindowsReleaseTests.cs` only for a demonstrated missing assertion
- Verify: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`, `src/InvoiceFlowAI.App/Rpc/`, `src/InvoiceFlowAI.Infrastructure/Reports/`, `templates/`

**Interfaces:**

- The Windows integration test starts the real Avalonia NativeWebView, loads the packaged local page, performs `bridge.hello`, starts a run through `run.start`, observes progress events, requests cancellation or reaches completion, loads results, exports a workbook, and opens the report through the host action.
- Use deterministic fake IMAP/mailbox and provider transport implementations registered through test overrides. No real mailbox, credential, or provider request is permitted.
- Assert event sequences are contiguous, a terminal event follows finalization, report rows match persisted results, and no response/event contains secret fixtures or signed URL query values.

- [x] **Step 1: Add the opt-in native integration test** gated by `INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST=1`; assert the run RPC handshake and expected event sequence.
- [x] **Step 2: Run the focused app test** with `$env:INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST = "1"; dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~NativeWebViewRunIntegrationTests`; confirmed the initial integration exposed missing production DI registrations before the host path completed.
- [x] **Step 3: Complete test DI overrides** so the test exercises the real bridge and production run/report services without outbound network access; a deterministic empty mailbox session prevents provider calls.
- [x] **Step 4: Run the full focused .NET and frontend validation:**

```powershell
dotnet test tests/InvoiceFlowAI.Contracts.Tests/InvoiceFlowAI.Contracts.Tests.csproj --no-restore
dotnet test tests/InvoiceFlowAI.Domain.Tests/InvoiceFlowAI.Domain.Tests.csproj --no-restore
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore
node --test tests/InvoiceFlowAI.App.Tests/Rpc/*.test.mjs
node --check templates/rpc_client.js
node --check templates/bridge-bootstrap.js
$env:INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST = "1"
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~NativeWebViewRunIntegrationTests
dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj --configuration Release --no-restore
git diff --check
```

### Task 9: Retire Python Desktop Runtime, DSH Plugin, and macOS Desktop Release

**Files:**

- Delete: `plugins/dsh-invoice-downloader/` (entire directory, as explicitly approved)
- Delete: `build/windows/runtime_hook_playwright.py` and its obsolete packaging test
- Delete: `build/windows/InvoiceFlowAI.spec`
- Delete: `build/macos/InvoiceFlowAI.spec`
- Delete: `build/macos/build_release.sh`
- Modify: `.github/workflows/windows.yml`
- Delete: obsolete PyInstaller manifest/release-builder assertions from `tests/test_run_evidence.py`
- Modify: `requirements.txt` to retain dependencies used by reference tests and developer tooling
- Modify: `README.md`, `README.en.md`, and any release documentation that advertises macOS desktop or PyInstaller artifacts
- Modify: `tests/InvoiceFlowAI.App.Tests/EndToEnd/WindowsReleaseTests.cs`
- Create: `src/InvoiceFlowAI.Application/Release/NoPythonRuntimeReleaseAudit.cs`
- Verify/delete: Python packaging/runtime modules only when no retained reference test, truth-data builder, or audit tool consumes them

**Interfaces:**

- The .NET publish output remains self-contained `win-x64` and includes the C# URL recovery worker, OCR model, browser/runtime assets, release manifest, and licenses already managed by `publish_dotnet_app.ps1`.
- The release audit rejects Python source/bytecode, interpreters, venvs, Python runtime DLLs/archives, PyInstaller bundles, and Python.NET artifacts from published artifacts.
- The installer, portable package, and Windows workflow target the Avalonia executable rather than a Python packager output.
- Remove macOS desktop build/release paths and public download claims. Do not remove unrelated macOS references for non-desktop tooling without checking their scope.
- Preserve Python reference modules consumed by retained tests, truth-data builders, or audit tools; they are no longer included or invoked by the product build/release path.

- [x] **Step 1: Add a release-artifact audit test** that rejects Python/venv/PyInstaller artifacts and confirms the published app/worker executable names; run it against a synthetic publish tree.
- [x] **Step 2: Run the focused audit tests** for injected Python markers and the configured publish tree.
- [x] **Step 3: Update the Windows release workflow** to publish the Avalonia app and worker, then audit the actual publish tree. No Python executable is invoked by this workflow.
- [x] **Step 4: Search consumers of Python product modules and DSH runtime**; retain modules used by explicitly retained reference tests/tooling and remove the obsolete Python packaging hook/tests.
- [x] **Step 5: Remove PyInstaller/macOS desktop packaging and release workflows;** update README release instructions and links to Windows x64 C# artifacts only.
- [x] **Step 6: Build and qualify the Windows x64 publish directory:**

```powershell
pwsh -File build/windows/publish_dotnet_app.ps1 -Configuration Release -PublishRoot publish/win-x64
$env:INVOICEFLOWAI_PUBLISH_ROOT = (Resolve-Path publish/win-x64).Path
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --no-restore --filter FullyQualifiedName~Configured_publish_tree_passes_no_python_runtime_release_audit
dotnet test InvoiceFlowAI.sln --no-restore
git diff --check
```

- [x] **Step 7: Run the product-copy/release audit** for stale macOS download links and Python desktop instructions; confirm no user-visible implementation notes were introduced.

---

## Plan Self-Review

- **Spec coverage:** Task 1 defines the strict typed contract; Tasks 2–3 wire run admission, execution, durable events, progress, and cancellation; Task 4 completes the currently missing report data source/stage; Tasks 5–7 cover every remaining run/results/report/file/window frontend operation; Task 8 validates the real NativeWebView path; Task 9 removes the Python desktop/DSH/macOS release surfaces and verifies Python-free publishing.
- **Placeholder scan:** The plan contains no `TBD`, `TODO`, or “implement later” steps. The old Python modules are explicitly classified by consumer before deletion, so unrelated developer utilities are not removed by filename guesswork.
- **Type consistency:** New run DTOs are defined before handlers consume them. `RunInput`, `PipelineRunFactory`, `RunCoordinator`, `RunProgressPayload`, `ReportRunData`, `ReportExportWorkItem`, and `ReportExportRpcResult` are existing types. The plan adds a single `IDesktopRunService`, `IRunEventPublisher`, `EfReportRunDataStore`, and `ReportExportStage` boundary; handlers consume those services rather than introducing duplicate pipeline or workbook logic.
- **Known implementation gap recorded:** `ClosedXmlReportExporter` and `ReportApplicationService` exist, but production `IReportRunDataSource`, `IReportPathStore`, and `IReportExportStage` implementations/registrations were not found. Task 4 treats those as missing and validates real DI resolution instead of assuming they are already complete.
- **Commit policy:** No task creates a commit; implementation changes remain uncommitted unless the user asks otherwise.
