# CWT Cancellation Parity Implementation Plan

> **For agentic workers:** Execute each task with a focused failing test first, then the smallest implementation change. Do not commit; the user explicitly requested no commits.

**Goal:** Migrate the existing Python CWT cancellation classification, registration, post-archive hotel-confirmation matching, and review routing into the C# desktop pipeline.

**Architecture:** Preserve only the sender/subject context needed for the in-memory run by associating each candidate with its `MailboxMessage`. Identify CWT cancellation candidates using the established CWT sender-domain and filename rules, archive them as manual review, and execute one idempotent finalization pass after ordinary archive and pairing work. Update the durable archive path and review item through explicit store contracts; write a sanitized relationship sidecar using stable document/artifact IDs rather than personal names or local absolute paths.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, `CandidateCollectionStage`, `DocumentArchivingStage`, archive/manual-review/pairing stores, `IArchiveFileSystem`.

## Global Constraints

- Use synthetic sender addresses, names, and file content only.
- Do not persist message body or raw URL/capability query for CWT matching.
- Preserve original document identity and content hash; do not add category/history mutations absent from Python behavior.
- Malformed, ambiguous, or failed matches must not move an unrelated artifact; expose stable safe reason codes.
- CWT finalization runs at most once per archive-stage execution and remains idempotent after retry.
- Do not commit.

---

### Task 1: Candidate Message Signal

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Pipeline/CandidateCollectionStage.cs`
- Modify: `tests/InvoiceFlowAI.Application.Tests/Pipeline/CandidateCollectionStageTests.cs`
- Modify only if needed: `src/InvoiceFlowAI.Domain/Candidates/DocumentCandidate.cs`

- [ ] Add synthetic mailbox messages with CWT sender, non-CWT sender, and identical attachment UID; assert candidate metadata contains only the sender and subject required for classification and remains associated with the right candidate.
- [ ] Run the focused candidate collection test and confirm the metadata assertion fails before implementation.
- [ ] Add metadata projection by `(Mailbox, UID)` without copying body content or URL query strings; preserve existing candidate ordering and identity.
- [ ] Re-run candidate collection tests.

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~CandidateCollectionStageTests`
Expected: all selected tests pass.

### Task 2: Cancellation Recognition and Registration

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageTests.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/DocumentArchivingStage.cs`
- Modify if required: `src/InvoiceFlowAI.Application/Archive/IArchiveNamingPolicy.cs` / `ArchiveNamingPolicy.cs`

- [ ] Add tests for a CWT-domain candidate whose filename contains `取消知会`, a non-CWT candidate with the same filename, and a CWT candidate with a non-cancellation filename.
- [ ] Assert only the CWT cancellation candidate is routed to manual review with `CWT_HOTEL_CANCELLATION` and is registered before final matching; no sender/subject is written to archive metadata.
- [ ] Run the focused archiving tests red, then implement the minimal classification and registration behavior using the current candidate metadata and archive naming policy.
- [ ] Re-run `DocumentArchivingStageTests`.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DocumentArchivingStageTests`
Expected: all selected tests pass.

### Task 3: Durable Matched-Artifact Relocation

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Archive/IArchiveArtifactStore.cs`
- Modify: `src/InvoiceFlowAI.Application/Archive/IArchiveFileSystem.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfArchiveArtifactStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/ArchivedArtifactRow.cs` only if current path fields cannot represent the relocation
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/DocumentArchivingStage.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageTests.cs`
- Modify: EF migration/model snapshot only if persistence schema must change

- [ ] Add a failing test that archives a synthetic CWT cancellation plus one eligible same-name hotel confirmation and asserts exactly that confirmation moves to a review path while retaining artifact ID and SHA-256.
- [ ] Add negative cases for non-hotel files, name mismatch, malformed cancellation name, destination collision, and unsafe relative path; assert no unrelated move.
- [ ] Assert the artifact store snapshot records the review path, `Committed` content identity remains unchanged, and an open `ManualReviewItem` uses `CWT_CANCELLATION_MATCH`.
- [ ] Add the narrow store/filesystem operation needed for transactional metadata plus atomic file relocation; add a migration only if the existing columns are insufficient.
- [ ] Re-run focused archive tests, then persistence store tests.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DocumentArchivingStageTests|FullyQualifiedName~EfArchiveArtifactStoreTests`
Expected: all selected tests pass.

### Task 4: Relationship Sidecar and Idempotent Finalization

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Archive/IArchiveFileSystem.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/PhysicalArchiveFileSystem.cs`
- Create: `src/InvoiceFlowAI.Application/Archive/CwtCancellationFinalizer.cs` (or the established archive service folder if the ownership check places it in Infrastructure)
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/DocumentArchivingStage.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageTests.cs`

- [ ] Add a failing test that the finalizer writes a sidecar containing `CWT_CANCELLATION_MATCH`, cancellation document ID, confirmation document ID, content hash, and timestamp, with no absolute paths, sender, subject, person name, or source URL.
- [ ] Assert every eligible same-name hotel confirmation is moved in candidate order, repeated finalizer invocation does not duplicate moves/review items/sidecars, and a failed sidecar write returns a safe warning without undoing an already committed move.
- [ ] Invoke finalization once after all ordinary archive and pair writes complete; check cancellation token before each mutation.
- [ ] Re-run focused finalizer/archive tests, then all `Archive` tests.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Archive`
Expected: all selected tests pass.

### Task 5: Parity Matrix

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/DesktopParityFixtureTests.cs`

- [ ] Add a synthetic CWT fixture covering sender classification, registration order, deterministic multi-match and no-match decisions, review reason, path/hash identity, and safe sidecar content.
- [ ] Run fixture tests red before adding any production changes they require; then run the fixture and focused archive tests green.
- [ ] Mark only directly asserted CWT behaviors verified; retain any historical trace/history behavior without matching C# contract as partial rather than claiming parity.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DesktopParityFixtureTests|FullyQualifiedName~DocumentArchivingStageTests`
Expected: all selected tests pass and matrix status matches the fixture evidence.
