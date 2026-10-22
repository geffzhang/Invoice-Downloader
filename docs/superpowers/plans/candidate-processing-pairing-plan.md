# Candidate Processing and Pairing Implementation Plan

> **For agentic workers:** Use the `superpowers:executing-plans` skill to implement this plan task-by-task. Execute each task with red-green-refactor and keep the native URL recovery work in its own plan.

**Goal:** Collect stable attachment and URL candidates, preserve preflight terminal outcomes through extraction, and run deterministic same-mailbox pairing with the approved manual-review mapping.

**Architecture:** Application services own candidate identity, collection policy, and pairing projection. Infrastructure supplies a narrow processing-history reader over `DocumentProcessingRow`. Candidate and pairing data remain in the existing pipeline packets; `PairingEngine` is reused unchanged.

**Tech Stack:** .NET 10, C# records, SHA-256/HMAC-SHA-256, EF Core, xUnit, FluentAssertions.

## Global Constraints

- Use the existing .NET SDK pinned by `global.json`; do not change the SDK pin.
- Candidate identity includes account, mailbox, UIDVALIDITY, message UID, resource kind, and resource fingerprint.
- Attachment resource fingerprints are SHA-256 over payload bytes. URL resource fingerprints are HMAC-SHA-256 over the full canonical URL, using a stable application-managed secret and an explicit key version.
- Never put raw URL/query data, email bodies, credentials, OCR text, or raw exception messages into persisted identity, metadata, trace, or failure messages.
- Drop decisions are filtered before candidate outcomes. `retain_only`, `manual_review`, `skip`, and duplicate/history decisions produce explicit terminal results.
- Pairing only considers successfully resolved Ride/Hotel pairable types from the same mailbox in the current run; unmatched and ambiguous pairable results become `ManualReview`.
- Do not modify the existing `PairingEngine` matching algorithm.

---

### Task 1: Carry source account and preflight terminal results

**Files:**

- Modify: `src/InvoiceFlowAI.Application/Mail/MailboxScanner.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineContracts.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/Nodes/BuiltInPipelineNodes.cs` only if packet construction requires it
- Test: `tests/InvoiceFlowAI.Application.Tests/Mail/MailboxUrlCandidateContractTests.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pipeline/CandidateBatchContractTests.cs`

**Interfaces:**

- `MailboxScanResult.AccountId` is an init-only property defaulting to the empty string so its five-argument constructor stays source-compatible.
- `CandidateBatch` becomes `CandidateBatch(IReadOnlyList<CandidateWorkItem> Items, IReadOnlyList<CandidateProcessResult>? TerminalResults = null)`; a convenience property exposes an empty list when omitted.
- `ExtractionBatch` retains its one-argument constructor and gains an init-only `PreflightResults` property defaulting to an empty list.
- Collection terminal results are merged with extractor results by the extraction stage in the later extraction plan; URL recovery must preserve them unchanged.

- [ ] **Step 1: Write failing compatibility tests**

Assert that `MailboxScanResult.AccountId` defaults empty and can be set with a `with` expression; existing five-argument construction still compiles. Assert that `CandidateBatch(items)` exposes an empty terminal-results list and that an explicitly supplied terminal result is retained. Assert the same one-argument compatibility and empty default for `ExtractionBatch.PreflightResults`.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateBatchContractTests`

Expected: compile failure for the missing properties and second constructor parameter.

- [ ] **Step 3: Add the compatible packet properties**

Add the two init-only scan/extraction properties in record bodies. Add the optional second `CandidateBatch` positional parameter with a null default and expose a non-null `EffectiveTerminalResults` property that returns `TerminalResults ?? Array.Empty<CandidateProcessResult>()`. Update the URL-recovery fake/pass-through test to prove terminal results survive an unchanged recovery stage.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateBatchContractTests`

Expected: all contract tests pass and existing pipeline adapter tests remain source-compatible.

### Task 2: Add candidate identity and history contracts

**Files:**

- Create: `src/InvoiceFlowAI.Application/Candidates/ICandidateIdentityKeyProvider.cs`
- Create: `src/InvoiceFlowAI.Application/Candidates/CandidateIdentityFactory.cs`
- Create: `src/InvoiceFlowAI.Application/Persistence/ICandidateHistoryReader.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Candidates/CandidateIdentityFactoryTests.cs`

**Interfaces:**

- `ICandidateIdentityKeyProvider.GetCurrentKeyAsync(CancellationToken)` returns a key version and key bytes from the stable secret store.
- `CandidateIdentityFactory.CreateAttachment(accountId, mailbox, uidValidity, messageUid, fileName, payload)` returns an opaque `DocumentIdentity` and uses SHA-256 of payload bytes.
- `CandidateIdentityFactory.CreateUrlAsync(accountId, mailbox, uidValidity, messageUid, sourceUrl, providerGroupId, cancellationToken)` returns an opaque identity whose resource fingerprint is HMAC-SHA-256 over a canonical URL and whose value records the key version.
- `ICandidateHistoryReader.ExistsAsync(DocumentIdentity, CancellationToken)` returns true only for a completed, non-retryable prior processing result.

- [ ] **Step 1: Write failing identity tests**

Cover stable repeat identity; separation across account, mailbox, UIDVALIDITY, message UID, and resource kind; distinct attachment payload hashes; URL query distinction; URL fragment exclusion; stable key-version inclusion; and ensure neither raw URL nor query token appears in `DocumentIdentity.Value`.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateIdentityFactoryTests`

Expected: compile failure because the identity and key-provider abstractions do not exist.

- [ ] **Step 3: Implement the identity factory**

Canonicalize URL scheme and IDN host to lowercase, preserve explicit port/path/query, and omit only the fragment. Hash a length-prefixed encoding of the source tuple and resource fingerprint; use HMAC key bytes only for URL/group fingerprints. Do not expose or log key bytes.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateIdentityFactoryTests`

Expected: all identity tests pass, including secret-text non-disclosure.

### Task 3: Implement candidate collection and history lookup

**Files:**

- Create: `src/InvoiceFlowAI.Application/Pipeline/CandidateCollectionStage.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfCandidateHistoryReader.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pipeline/CandidateCollectionStageTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/EfCandidateHistoryReaderTests.cs`

**Interfaces:**

- `CandidateCollectionStage` implements `ICandidateCollectionStage` and consumes `MailboxScanResult`, `IAttachmentCandidatePolicy` decisions already attached to MIME candidates, `CandidateIdentityFactory`, and `ICandidateHistoryReader`.
- `EfCandidateHistoryReader` queries `DocumentProcessingRow` by opaque document ID and returns true only when `CompletedAtUtc` is set and `Retryable` is false.
- Candidate ordering is deterministic: source message order, then attachment/link order, with a single increasing run sequence.

- [ ] **Step 1: Write failing collection tests**

Cover main-chain attachment payload preservation, URL candidate projection, source mailbox/account/UID context, drop filtering, retain-only terminal output, manual-review terminal output, skip as Duplicate, in-run identity/history duplicate as Duplicate, history-reader duplicate, cancellation propagation, and deterministic sequence assignment.

- [ ] **Step 2: Run focused collection tests and verify RED**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateCollectionStageTests`

Expected: compile failure because the concrete stage is not registered or implemented.

- [ ] **Step 3: Implement the collection stage and history reader**

Filter `drop` attachments without creating work items. Emit `Retained`, `ManualReview`, or `Duplicate` `CandidateProcessResult` values in `CandidateBatch.TerminalResults` for the corresponding preflight actions. Emit only main-chain attachments and URL candidates as `CandidateWorkItem` values. Check cancellation before each candidate and before each history lookup. Persist no raw URL or body value in trace or metadata.

- [ ] **Step 4: Register stage dependencies and verify focused suites**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateCollectionStageTests`

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~EfCandidateHistoryReaderTests`

Expected: both focused test groups pass.

### Task 4: Implement same-mailbox pairing projection and status mapping

**Files:**

- Create: `src/InvoiceFlowAI.Application/Pairing/ArtifactPairingStage.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pairing/ArtifactPairingStageTests.cs`

**Interfaces:**

- `ArtifactPairingStage` implements `IArtifactPairingStage` and depends on `IPairingEngine`.
- It consumes only `Resolved` results with an `InvoiceDocument` and one of the four supported document types; provider, seller tokens, source UID, mailbox, path, amount, and date are projected from existing candidate/invoice values.
- It returns deterministic `PairingResult` values and a complete candidate result list; unmatched and ambiguous pairable candidates become `ManualReview` with safe failure reason codes `PAIRING_UNMATCHED` and `PAIRING_AMBIGUOUS`.

- [ ] **Step 1: Write failing pairing-stage tests**

Cover all four type-to-role mappings, amount/date/provider/merchant/source projection, grouping by mailbox, a unique match, unmatched ride and hotel results, an ambiguous component, pass-through of non-pairable types, and pass-through of non-Resolved results.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~ArtifactPairingStageTests`

Expected: compile failure because `ArtifactPairingStage` does not exist.

- [ ] **Step 3: Implement projection and status mapping**

Map `TotalAmount ?? Amount` and `InvoiceDate`; normalize merchant tokens from seller; use only trusted extracted provider metadata; group by candidate metadata mailbox; invoke `IPairingEngine` once per family/mailbox group. Preserve pair assignments and update only eligible unmatched/ambiguous candidate results using record `with` expressions.

- [ ] **Step 4: Register and run the Application suite**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj`

Expected: all Application tests pass; existing pairing-engine tests remain unchanged.

## Handoff

After this plan, create and execute the separate native URL recovery plan covering link extraction, public-address/redirect/peer enforcement, bounded worker process lifecycle, provider implementations, and Playwright browser fallback. The extraction/archive implementation must merge `CandidateBatch.TerminalResults` and `ExtractionBatch.PreflightResults` before archive processing.