# IMAP Fetch Failure Diagnostics Implementation Plan

> **For agentic workers:** Use the executing-plans workflow task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface per-UID IMAP FETCH failures as safe, replayable, non-terminal diagnostics in the Processing Center without changing successful message processing or run terminal status.

**Architecture:** Capture `MailboxScanResult.FetchFailures` in the existing pipeline capture node and expose them through `PipelineRun`. Carry the safe `{ uid, reasonCode }` values on the existing persisted `run.progress` contract and its progress snapshot; map and display them in the Processing Center. The XLSX schema and run failure/terminal contracts remain unchanged.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, React-in-template JavaScript, Node.js `node:test`, Babel compiler bundled in the repository.

## Global Constraints

- Only numeric UID and the fixed safe code `IMAP_MESSAGE_FETCH_FAILED` may be shown; never include message data, mailbox credentials, source URLs, query strings, or exception text.
- These diagnostics are not `RunFailure` values and do not change terminal status, terminal failure counts, candidate error counts, UID cursor advancement, or archive decisions.
- The new progress field is optional and defaults to an empty list so old event JSON remains deserializable.
- The chosen surface is the Processing Center using the existing `run.progress` event and snapshot/replay path; do not change the report workbook schema.
- Do not commit changes or delete the Python runtime as part of this diagnostic feature.

---

## File Responsibilities

- `src/InvoiceFlowAI.Contracts/Rpc/RunMailboxFetchFailureDiagnostic.cs`: transport-safe diagnostic DTO with `Uid` and `ReasonCode`.
- `src/InvoiceFlowAI.Contracts/Rpc/RunProgressPayload.cs`: optional event field, default empty.
- `src/InvoiceFlowAI.Contracts/Rpc/RunProgressSnapshot.cs`: optional replay field, default empty.
- `src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs`: capture scan failures and expose them from `PipelineRun`.
- `src/InvoiceFlowAI.Application/Runs/RunExecutionService.cs`: copy captured safe diagnostics onto every progress event after scan capture.
- `src/InvoiceFlowAI.Application/Runs/DesktopRunService.cs`: project latest persisted progress diagnostics into the RPC snapshot.
- `templates/run_page_rpc.js`: map snapshot and live progress diagnostics into page state.
- `templates/index_app.js`: render the non-terminal warning and per-UID safe reason codes in the Processing Center.
- `templates/index_app.compiled.js`: generated runtime page, rebuilt only with `node build/compile-webview-page.mjs`.
- `tests/InvoiceFlowAI.Application.Tests/Runs/RunExecutionServiceTests.cs`: prove the failure list survives capture and progress publication while the run still completes.
- `tests/InvoiceFlowAI.Application.Tests/Runs/DesktopRunServiceTests.cs`: prove persisted progress replay returns the diagnostic list.
- `tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs`: prove optional snapshot/live-event mapping, replay, and backward compatibility.
- `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`: close the FetchFailures presentation row and mark recursive batch-splitting `approved retired` per user decision.

## Task 1: Carry Diagnostics Through the Application Progress Contract

**Files:**

- Create: `src/InvoiceFlowAI.Contracts/Rpc/RunMailboxFetchFailureDiagnostic.cs`
- Modify: `src/InvoiceFlowAI.Contracts/Rpc/RunProgressPayload.cs`
- Modify: `src/InvoiceFlowAI.Contracts/Rpc/RunProgressSnapshot.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs`
- Modify: `src/InvoiceFlowAI.Application/Runs/RunExecutionService.cs`
- Modify: `src/InvoiceFlowAI.Application/Runs/DesktopRunService.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Runs/RunExecutionServiceTests.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Runs/DesktopRunServiceTests.cs`

- [ ] **Step 1: Add a failing run integration test**

In `RunExecutionServiceTests`, register a scanner that returns two successful messages plus `FetchFailures = [new MailboxFetchFailure(7, "IMAP_MESSAGE_FETCH_FAILED")]`. Execute the normal empty-candidate pipeline. Assert that a published `RunProgressPayload` contains exactly UID 7 and the fixed code, that the final event remains `run.terminal` with `RunState.Completed`, and that no `RunFailure` is introduced for the fetch diagnostic.

- [ ] **Step 2: Run the test and confirm the missing propagation**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --filter FullyQualifiedName~RunExecutionServiceTests --nologo
```

Expected: the new assertion fails because `MailboxScanCaptureNode` currently retains only `EmailCount` and `RunProgressPayload` has no fetch diagnostic field.

- [ ] **Step 3: Add the backward-compatible transport contracts and capture field**

Add this contract:

```csharp
namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunMailboxFetchFailureDiagnostic(long Uid, string ReasonCode);
```

Add an initialized, non-positional property to both `RunProgressPayload` and `RunProgressSnapshot`:

```csharp
public IReadOnlyList<RunMailboxFetchFailureDiagnostic> MailboxFetchFailures { get; init; }
    = Array.Empty<RunMailboxFetchFailureDiagnostic>();
```

`MailboxScanCaptureNode` stores `MailboxScanResult.FetchFailures` when it captures the packet. `PipelineRun.MailboxFetchFailures` exposes the captured list. Existing positional constructors remain unchanged.

- [ ] **Step 4: Include captured diagnostics in progress events and snapshots**

In `RunExecutionService`, after each `ExecuteStepAsync`, project `run.MailboxFetchFailures` to `RunMailboxFetchFailureDiagnostic`, preserving UID order and emitting the literal safe code `IMAP_MESSAGE_FETCH_FAILED`; assign the same snapshot to the `RunProgressPayload` for that cycle and subsequent candidate progress events. Do not add a `RunFailure` or alter progress `Stats.Errors`.

In `DesktopRunService`, extend its private `ProgressProjection` with the latest progress diagnostic list, then initialize `RunProgressSnapshot.MailboxFetchFailures` from that projection. `EmptyProgress()` and deserialization of older progress JSON must yield an empty list.

- [ ] **Step 5: Verify Application tests**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --filter "FullyQualifiedName~RunExecutionServiceTests|FullyQualifiedName~DesktopRunServiceTests" --nologo
```

Expected: all selected tests pass; the new integration assertion sees the diagnostic on progress and a completed terminal event.

## Task 2: Render Diagnostics in the Processing Center

**Files:**

- Modify: `templates/run_page_rpc.js`
- Modify: `templates/index_app.js`
- Regenerate: `templates/index_app.compiled.js`
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs`

- [ ] **Step 1: Add failing Node tests for snapshot and live progress mapping**

Extend `run_page_rpc.test.mjs` to assert that `getProgress()` maps a populated `mailboxFetchFailures` array, an older snapshot without the field maps to `[]`, and buffered/replayed `run.progress` events retain the failures through a later `run.terminal` event without changing its terminal state.

- [ ] **Step 2: Run the Node tests and confirm the missing mapping**

Run:

```powershell
node --test tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs
```

Expected: the new mapping assertions fail before the mapper is extended.

- [ ] **Step 3: Map the optional list and render a non-terminal warning**

In `run_page_rpc.js`, map `mailboxFetchFailures` to `mailbox_fetch_failures` for snapshots. In `applyEvent`, replace the current list only when the progress payload supplies an array; otherwise retain the current list so older events do not erase diagnostics.

In `index_app.js`, initialize `DEFAULT_PROGRESS.mailbox_fetch_failures` to `[]`. In the Processing Center progress card, render a warning only when the list is non-empty. The message must say that some mail messages could not be read while remaining successful messages continue; list each `UID` with its safe reason code. Do not put the run in an error tone or change `stats.errors`.

Regenerate the runtime bundle with:

```powershell
node build/compile-webview-page.mjs
```

- [ ] **Step 4: Verify the page mapper and generated bundle**

Run:

```powershell
node --test tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs
node build/compile-webview-page.mjs --check
```

Expected: Node tests pass and the checked-in compiled page matches the source.

## Task 3: Close the IMAP Parity Rows and Revalidate

**Files:**

- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`
- Verify: all files above and the existing retained Python whitelist.

- [ ] **Step 1: Update matrix statuses after focused tests pass**

Mark `IMAP per-UID fetch failure diagnostic presentation` as `verified`, citing the Application progress/replay tests and Processing Center RPC tests. Mark `IMAP recursive split for failed/malformed batched FETCH responses` as `approved retired`; the approved replacement contract is one full-message request per UID with bounded retry, so failure of one UID cannot discard neighboring messages. Keep the strict SEARCH and INTERNALDATE wire rows `verified`.

- [ ] **Step 2: Run final regression gates**

Run:

```powershell
Remove-Item Env:INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST -ErrorAction SilentlyContinue
dotnet test InvoiceFlowAI.sln -c Release --nologo
node --test tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs
node build/compile-webview-page.mjs --check
e:/GitHub/Invoice-Downloader/.venv/Scripts/python.exe -m pytest -q tests/test_audit_email_truth.py tests/test_build_truth_dataset_imap.py tests/test_strict_truth_audit.py tests/test_batch_validation.py tests/test_artifact_verifier.py tests/test_model_calibration.py tests/test_xml_parity.py tests/test_pinned_http.py tests/test_url_security.py
```

Expected: all .NET tests pass with only the two opt-in NativeWebView tests skipped; Node tests pass; compiled page check is clean; retained Python whitelist passes; parity scan returns no `partial`, `gap`, or `decision needed` rows. Do not delete the Python runtime in this feature plan.

## Plan Self-Review

- Every design requirement maps to a task: capture and transport, persisted replay, UI display, privacy, terminal invariance, backward compatibility, and parity evidence.
- `RunProgressPayload` and `RunProgressSnapshot` receive body properties so existing positional construction remains source-compatible.
- The compiled WebView bundle is regenerated by the checked-in build script rather than edited manually.
- This plan intentionally stops before Task 7 runtime deletion; that remains governed by the main retirement plan and its independent deletion-reference gate.