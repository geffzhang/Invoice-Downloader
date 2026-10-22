# Pairing and Archive Orchestration Implementation Plan

> **For agentic workers:** Implement task-by-task with a fresh focused test cycle at each task. Keep the current dirty worktree intact. Do not commit changes.

**Goal:** Complete pairing review classification and the production pairing-to-archive path while preserving Python matching behavior and never moving or modifying `CandidateProcessResult.ArtifactPath`.

**Architecture:** Keep `PairingEngine` as the deterministic pure algorithm and add per-document review explanations without changing compatibility, weights, or optimization. Add an archive stage that asks the existing coordinator to copy an immutable source to an archive-owned sibling temp file, then atomically moves only that copy; persist temp paths and pairing state so recovery is truthful after a crash.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, EF Core, SQLite-backed `InvoiceFlowDbContext`, existing application/infrastructure DI and archive abstractions.

## Global Constraints

- Preserve the exact Python `pairing_engine.py` compatibility rules, edge weights, max-pair-count-then-max-score objective, ambiguity behavior, and deterministic output.
- `ArtifactPath` is immutable input. Only an archive-owned temporary copy may be passed to `AtomicMoveAsync`.
- The archive temp copy must be created beside its final destination on the same volume.
- Before a `Prepared` row commits, a failed copy may be cleaned; after it commits, recovery evidence must remain intact.
- A collision must not overwrite an existing destination.
- Candidate failures remain candidate-scoped; cancellation propagates; infrastructure failures are surfaced in `ArchiveBatch.Failures`.
- Do not add network downloads, change OCR/model behavior, or modify unrelated extraction behavior.
- Do not commit changes.

---

### Task 1: Add per-document pairing explanations without changing assignments

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Pairing/PairingDocument.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pairing/PairingEngineTests.cs`

**Interfaces:**
- Add `PairingReviewReasonCode` values `CounterpartMissing`, `NoCompatibleEdge`, `AmbiguousOptimum`, and `UnmatchedByGlobalAssignment` with stable external codes `PAIRING_COUNTERPART_MISSING`, `PAIRING_NO_COMPATIBLE_EDGE`, `PAIRING_AMBIGUOUS_OPTIMUM`, and `PAIRING_UNMATCHED_BY_GLOBAL_ASSIGNMENT`.
- Add `PairingDocumentReviewReason(string DocumentId, PairingReviewReasonCode Code, IReadOnlyList<string> EvidenceCodes)`.
- Add an optional `ReviewReasons` property to `PairingResult`, defaulting to an empty list to preserve existing construction call sites.
- Add the selected edge score to `PairingAssignment` with a default value so existing two-argument construction remains source-compatible.
- Use stable evidence codes `PROVIDER_MISMATCH`, `AMOUNT_OUT_OF_TOLERANCE`, `DATE_OUT_OF_TOLERANCE`, and `REQUIRED_AMOUNT_MISSING`; emit only evidence applicable to the current pair family and sort ordinally.

- [ ] **Step 1: Add failing engine tests** for each review reason, ambiguity precedence, evidence-code ordering, selected edge score, and unchanged assignment results. Use these cases:
  - no companions: `PAIRING_COUNTERPART_MISSING`;
  - companions with incompatible provider: `PAIRING_NO_COMPATIBLE_EDGE` plus `PROVIDER_MISMATCH`;
  - equal 2x2 component: all four IDs receive `PAIRING_AMBIGUOUS_OPTIMUM`, not the generic unmatched code;
  - two invoices competing for one compatible companion: selected invoice remains paired and the other gets `PAIRING_UNMATCHED_BY_GLOBAL_ASSIGNMENT`.
- [ ] **Step 2: Run the focused tests and confirm the new assertions fail while the existing assignment assertions still pass.**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PairingEngineTests"
```

- [ ] **Step 3: Implement diagnostics alongside the current graph and optimal-assignment computation.** Do not alter `Compatible`, `EdgeScore`, `ConnectedComponents`, or the objective comparator. For a no-edge document, inspect all opposite-role documents and collect applicable stable incompatibility codes in ordinal order. Assign ambiguity before classifying generic unmatched documents.
- [ ] **Step 4: Re-run the focused suite.**

Expected: all pairing engine tests pass, including the existing ride tax, hotel date, provider, stable-order, and merchant-token cases.

---

### Task 2: Map pairing explanations to candidate review failures

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Pairing/ArtifactPairingStage.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pairing/ArtifactPairingStageTests.cs`

**Interfaces:**
- `ArtifactPairingStage` consumes `PairingResult.ReviewReasons` and writes the matching stable reason code into `CandidateFailure.ReasonCode`.
- Safe messages are selected by reason code; evidence codes remain in the pairing result only and are never included in user-facing messages.

- [ ] **Step 1: Replace the two broad-code tests with focused expectations** for missing counterpart, incompatible edge, ambiguous optimum, and global assignment loss. Add a test that a paired result remains `Resolved`, and a test that non-pairable/terminal results pass through unchanged.
- [ ] **Step 2: Run `ArtifactPairingStageTests` and confirm the new classification assertions fail.**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ArtifactPairingStageTests"
```

- [ ] **Step 3: Replace the `reviewReasons` string dictionary with the engine-provided per-document reason record.** Preserve existing mailbox/family grouping, preflight result order, and candidate identity mapping. Do not include seller text, email subject, source path, or document content in safe messages or trace.
- [ ] **Step 4: Re-run both pairing test classes.**

Expected: all four reason codes appear only on eligible unmatched documents, with ambiguity taking precedence.

---

### Task 3: Make archive commits copy immutable sources and persist recovery paths

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Archive/ArchiveCommitRequest.cs`
- Modify: `src/InvoiceFlowAI.Application/Archive/IArchiveFileSystem.cs`
- Modify: `src/InvoiceFlowAI.Application/Archive/IArchiveArtifactStore.cs`
- Modify: `src/InvoiceFlowAI.Application/Archive/ArchiveCommitCoordinator.cs`
- Modify: `src/InvoiceFlowAI.Application/Archive/ArchiveRecoveryService.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/PhysicalArchiveFileSystem.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/EfArchiveArtifactStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/ArchivedArtifactRow.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- Add: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/20260925003448_PersistArchivePaths.cs`
- Add: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/20260925003448_PersistArchivePaths.Designer.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/InvoiceFlowDbContextModelSnapshot.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveCommitCoordinatorTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveRecoveryServiceTests.cs`

**Interfaces:**
- Change `ArchiveCommitRequest` to take immutable `SourceFilePath`, resolved absolute `FinalFilePath`, user-facing `FinalRelativePath`, and `FileName`; callers must no longer supply a source path as a temp path.
- Add `IArchiveFileSystem.CopyToSiblingTempAsync(string sourcePath, string finalFilePath, CancellationToken cancellationToken)`, returning the unique private copy path beside the final file.
- Extend `ArchiveArtifactSnapshot` and `ArchiveRecoveryEntry` with distinct `TempFilePath`, absolute `FinalFilePath`, and `FinalRelativePath` values. Persist distinct `TempFilePath` and `FinalFilePath` values on `ArchivedArtifactRow`; keep the existing relative path for results. Make new path columns nullable so old committed rows remain readable; recovery must not inspect path fallbacks for already-committed legacy rows.
- The coordinator owns temp-copy cleanup and phase transitions. The stage must not guess whether a prepared temp file is safe to delete.

- [ ] **Step 1: Add failing coordinator tests** proving source bytes survive success, same-key committed retry, source hash mismatch, final hash mismatch, and simulated interruption after `Prepared`. Assert only the private copy is moved. Add recovery tests that use distinct temp and final paths.
- [ ] **Step 2: Run the two archive test classes to verify the tests fail against the current source-moving contract and lost temp path.**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ArchiveCommitCoordinatorTests|FullyQualifiedName~ArchiveRecoveryServiceTests"
```

- [ ] **Step 3: Implement physical sibling-copy semantics and coordinator ownership.** The coordinator first checks the idempotency key; return immediately for an identical committed record, or resume a matching `Prepared` record from its recorded paths. For a new key, copy the source beside the resolved final file, hash the copy against `ExpectedContentHash`, and record both absolute paths in `Prepared`. Clean the private copy if copy/hash/phase-A persistence fails before `Prepared` commits. If a concurrent identical prepare wins, clean only this attempt's unprepared copy. If phase A has committed, retain its temp/final evidence on every later failure.
- [ ] **Step 4: Prevent overwrite collisions.** Remove `File.Replace` semantics. For a new key, an existing final path is a collision and is never overwritten. For a matching `Prepared` key, an existing final file may complete the commit only after its hash matches; a mismatch becomes `RecoveryRequired` without replacing the file.
- [ ] **Step 5: Persist and restore both absolute paths.** Add the nullable DB columns and migration; `EfArchiveArtifactStore.ToSnapshot` must read temp path, absolute final path, and relative final path independently. Existing committed rows remain readable and are excluded from recovery scans.
- [ ] **Step 6: Re-run focused archive tests and verify source-preservation assertions pass.**

Expected: source files remain byte-identical; prepared copies and final paths are distinct; recoveries can locate whichever file survived; collisions do not overwrite.

---

### Task 4: Add deterministic paired and review archive naming

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Archive/ArchiveNamingPolicy.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Archive/ArchiveNamingPolicyTests.cs`

**Interfaces:**
- Add `ArchivePairPaths(string InvoiceRelativePath, string CompanionRelativePath)`.
- Add `BuildPairRelativePaths(InvoiceDocument invoice, string invoiceSourceName, InvoiceDocument companion, string companionSourceName, string runId, int pairIndex, string family)`.
- Add `BuildReviewRelativePath(string runId, string documentId, string sourceName, string reasonCode)` and `BuildRetainedRelativePath(string runId, string documentId, string sourceName)`.

- [ ] **Step 1: Add failing naming tests** for Python-compatible ride and hotel stems, stable ordering/pair index, source extension preservation, sanitized reason codes, safe filenames, and collision-resistant document/hash suffixes.
- [ ] **Step 2: Run `ArchiveNamingPolicyTests` and confirm the new method calls fail to compile or assertions fail.**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ArchiveNamingPolicyTests"
```

- [ ] **Step 3: Implement naming from invoice dates/amounts, family, and source extensions.** Use provider metadata/legacy ride platform tokens only for the ride label; sanitize every user-controlled filename component. Keep current `BuildRelativePath` output unchanged for non-paired resolved invoices.
- [ ] **Step 4: Re-run the naming suite.**

Expected: repeated identical inputs return identical paths, distinct document identities do not collide, and all components remain relative to `archive/<runId>/`.

---

### Task 5: Persist pair assignments and unmatched manual-review reasons

**Files:**
- Add: `src/InvoiceFlowAI.Application/Persistence/IPairingStore.cs`
- Add: `src/InvoiceFlowAI.Application/Persistence/IManualReviewItemStore.cs`
- Add: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfPairingStore.cs`
- Add: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfManualReviewItemStore.cs`
- Modify: `src/InvoiceFlowAI.Application/Persistence/IUnitOfWork.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test: add focused store tests under `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/`

**Interfaces:**
- `IPairingStore.UpsertAsync(PairingRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)` persists invoice-anchored pair state, ordered companion IDs/revisions, integer total score, and reason code through existing `PairingRow`.
- `IManualReviewItemStore.UpsertOpenAsync(runId, documentId, processingRevision, reasonCode, transaction, cancellationToken)` persists one open reason per unmatched candidate using the existing unique index on `ManualReviewItems`.
- Add `TransactionPurpose.PairingCommit` for the stage-owned pairing/review transaction.

- [ ] **Step 1: Add failing SQLite-backed tests** for pair upsert idempotency, deterministic companion JSON ordering, update of `Prepared` to `Committed`/`RecoveryRequired`, and separate review rows for unmatched companions (without writing their IDs into `InvoiceDocumentId`).
- [ ] **Step 2: Run the focused persistence tests and confirm missing stores/registrations fail.**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PairingStoreTests|FullyQualifiedName~ManualReviewItemStoreTests"
```

- [ ] **Step 3: Implement EF stores using the caller's `EfUnitOfWork`.** Pair rows remain invoice-anchored; standalone companion reviews use `ManualReviewItemRow`. Do not create a new migration for existing `PairingRow`/`ManualReviewItemRow` columns unless model validation proves a required schema field is missing.
- [ ] **Step 4: Register stores and add a service-provider test that resolves both.**
- [ ] **Step 5: Re-run the focused persistence tests.**

Expected: repeated writes with the same run/document/revision key update deterministically and never create duplicate open review items.

---

### Task 6: Implement the production document archiving stage

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineContracts.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/Nodes/PipelineStageNode.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/Nodes/BuiltInPipelineNodes.cs`
- Add: `src/InvoiceFlowAI.Infrastructure/Archive/DocumentArchivingStage.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Modify: `tests/InvoiceFlowAI.Application.Tests/Pipeline/BuiltInPipelineNodeTests.cs`
- Test: add `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Pipeline/ExtractionServiceRegistrationTests.cs`

**Interfaces:**
- Add optional `OutputRoot` to `PipelineItem<T>` and preserve it through `PipelineStageNode<TInput,TOutput>`; `ScanMailboxNode` populates it from `RunInput.SavePath`.
- Add `ArchiveStageRequest(string RunId, string OutputRoot, PairingBatch Batch)` and make `IDocumentArchivingStage` consume that request. Replace the generic `ArchiveDocumentsNode` with a context-aware node that passes the enclosing `PipelineItem` run context and emits the same run/sequence/final metadata.
- Add `ArchiveArtifactOutcome(string DocumentId, ArchiveArtifactState State, string? RelativePath, string? ReasonCode)` and an `Artifacts` collection to `ArchiveBatch`; keep `CandidateProcessResult.ArtifactPath` unchanged as the immutable source path.
- `DocumentArchivingStage` uses `IArchiveNamingPolicy`, `IArchiveCommitCoordinator`, `IPairingStore`, `IManualReviewItemStore`, and `IUnitOfWorkFactory`; resolve every relative name under the request's normalized `OutputRoot`, reject path traversal, and pass the absolute destination separately from the relative result path.
- `ExecuteAsync(ArchiveStageRequest request, CancellationToken cancellationToken)` returns `ArchiveBatch` with candidate results in original sequence order and run-level failures isolated from candidate-level failures.

- [ ] **Step 1: Add stage tests** for resolved pair, resolved standalone, unmatched manual review, retained, unresolved with/without a source, duplicate, and cancellation. Assert source files survive every case; assert that the pair does not reach `Committed` until both archive artifacts commit.
- [ ] **Step 2: Add pipeline-context tests** that prove `RunId`, `OutputRoot`, sequence, and final-packet metadata survive from `ScanMailboxNode` through the archive node. Confirm the context and stage behavior are currently absent.

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~BuiltInPipelineNodeTests"
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DocumentArchivingStageTests|FullyQualifiedName~ExtractionServiceRegistrationTests"
```

- [ ] **Step 3: Implement stable per-candidate orchestration.** Build a document-ID lookup from `PairingBatch.CandidateResults`; process results in their original order. Compute each expected source hash through `IArchiveFileSystem` and reject a mismatch with any populated candidate content hash. For paired ride/hotel records, compute both relative names and safely resolved absolute paths, write `Prepared` pairing state, invoke the coordinator once per immutable source, and mark the pair committed only after both results are committed. For a partial commit, persist `RecoveryRequired`, return safe candidate outcomes, and leave all coordinator evidence untouched.
- [ ] **Step 4: Handle unmatched/retained/unresolved outcomes using the corresponding safe path and review reason.** Do not archive duplicates again. If an eligible source path is absent, return a stable candidate failure without fabricating an output path. Append one `ArchiveArtifactOutcome` per archived/review artifact and never rewrite `CandidateProcessResult.ArtifactPath`.
- [ ] **Step 5: Register coordinator, recovery service, naming policy, physical file system, stores, and `IDocumentArchivingStage` in Infrastructure DI.** Add service registrations only once and keep their intended lifetimes scoped/singleton according to injected state.
- [ ] **Step 6: Run the focused stage/DI tests.**

Expected: each input candidate produces one ordered result; pair state is committed only after all members commit; candidate failures do not abort other candidates; no source path is ever passed as a move source.

---

### Task 7: Reconcile partial-pair recovery and run the complete release gate

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Archive/ArchiveRecoveryService.cs`
- Modify: `src/InvoiceFlowAI.Application/Archive/IArchiveArtifactStore.cs`
- Modify: `src/InvoiceFlowAI.Application/Persistence/IPairingStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/EfArchiveArtifactStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfPairingStore.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveRecoveryServiceTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageTests.cs`
- Review only: `docs/superpowers/specs/2026-09-25-pairing-archive-orchestration-design.md`

- [ ] **Step 1: Add failing crash/recovery tests** for crash before copy prepare, after `Prepared`, after one pair member commits, and after both files move but before pair-state update. Include a retry using the same archive key and assert the original sources remain unchanged.
- [ ] **Step 2: Run only archive recovery/stage tests and confirm the pair row does not currently reconcile with artifact recovery.**

Run:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ArchiveRecoveryServiceTests|FullyQualifiedName~DocumentArchivingStageTests"
```

- [ ] **Step 3: Reconcile pair state after artifact recovery.** After each artifact resolution, reload that run's `ArchiveArtifactSnapshot` collection and invoke `IPairingStore.ReconcileArchiveStateAsync` inside an `ArchiveCommit` UoW. When every invoice/companion artifact for a prepared pair is `Committed`, mark the pair committed; if any is `RecoveryRequired`, keep the pair recoverable with a stable reason; if evidence is missing or still `Prepared`, keep it recoverable. Never delete the source or remaining evidence.
- [ ] **Step 4: Run all focused pairing and archive tests.**

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PairingEngineTests|FullyQualifiedName~ArtifactPairingStageTests|FullyQualifiedName~ArchiveNamingPolicyTests"
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Archive"
```

- [ ] **Step 5: Run the full solution tests serially.**

```powershell
dotnet test InvoiceFlowAI.sln --no-restore -m:1
```

Expected: all tests pass. Record the existing Installer `Debug|Any CPU` configuration warning if it remains; do not alter unrelated solution configurations.

- [ ] **Step 6: Run the Windows x64 Release publish gate and verify the release output.**

```powershell
pwsh -File build/windows/publish_dotnet_app.ps1 -Configuration Release -PublishRoot publish/win-x64 -ProductVersion 1.0.0
```

Expected: publish succeeds; the existing model bundle and both manifests remain valid; no source artifacts are moved by this task's tests or publish process.

- [ ] **Step 7: Run the explicit Release build gate.**

```powershell
dotnet build InvoiceFlowAI.sln -c Release --no-restore -m:1
```

- [ ] **Step 8: Review `git diff --check`, all changed persistence paths, and the final diff for source-path mutation, sensitive review messages, path traversal/collisions, and unrelated edits.**

## Self-Review

- Compatibility weights and assignment objective: Task 1 plus parity vectors.
- Per-document manual-review codes and safe mapping: Tasks 1–2 and 5.
- Source preservation, same-volume temp copy, hash validation, no overwrite, and durable temp-path recovery: Task 3.
- Python-compatible paired naming: Task 4.
- Durable pairing/review state and DI: Tasks 5–6.
- Partial-pair crash recovery and all specified release gates: Task 7.
- Original `ArtifactPath` mutation is explicitly forbidden globally and asserted across tests.