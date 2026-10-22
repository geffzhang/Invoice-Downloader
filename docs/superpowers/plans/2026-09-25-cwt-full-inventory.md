# CWT Full Archive Inventory Implementation Plan

> **For agentic workers:** Implement task-by-task with focused red/green tests. Keep the current checkout; do not create a branch or worktree. Do not commit or force-add ignored plan files.

**Goal:** Match Python CWT cancellation behavior against the complete accommodation archive inventory, including artifacts from earlier runs and direct files in the legacy accommodation folder that have no artifact row.

**Architecture:** Extend committed C# artifact metadata with the original source filename and build an inventory from all runs plus direct legacy-folder files. Represent unmatched legacy files with a dedicated durable inventory record rather than a fabricated document identity. Relocations preserve safe sidecars, update truthful locations transactionally, and remain discoverable by recovery after a move or database failure.

**Tech Stack:** .NET 10, EF Core SQLite, existing archive store and filesystem abstractions, xUnit, FluentAssertions.

## Global Constraints

- Match Python's direct-child scan of `住宿发票`; do not recurse into subdirectories.
- Match by cancellation-derived person substring and the existing hotel marker; do not add parsed-document-type filtering to filename matching.
- Persist legacy source filename and relative path only in the local database; do not place names or absolute paths in sidecars, audit payloads, logs, or user-visible diagnostics.
- Keep deterministic destination collision behavior and fail closed when paths, hashes, or identities are ambiguous.
- Keep relocation failures recoverable and preserve the original content hash.
- Do not commit or force-add ignored plan files.

---

### Task 1: Preserve Source Filename on Archive Rows

**Files:**
- Modify `src/InvoiceFlowAI.Application/Archive/ArchiveCommitRequest.cs`
- Modify `src/InvoiceFlowAI.Application/Archive/IArchiveArtifactStore.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Archive/DocumentArchivingStage.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/ArchivedArtifactRow.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- Modify `src/InvoiceFlowAI.Application/Archive/ArchiveCommitCoordinator.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Archive/EfArchiveArtifactStore.cs`
- Add an EF migration and update `src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/InvoiceFlowDbContextModelSnapshot.cs`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageIntegrationTests.cs`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageIntegrationTests.cs`

- [ ] Add a SQLite regression that archives a standalone accommodation document and asserts the persisted row retains `Candidate.OriginalFileName`, independently of the generated final archive basename.
- [ ] Run the focused regression and confirm it fails because current rows only store `Path.GetFileName(relativePath)`.
- [ ] Add nullable `SourceFileName` to the commit request, snapshot, and row; pass `SourceName(result)` at the archive-stage boundary and persist it without changing the final archive filename.
- [ ] For pre-migration rows, have inventory projection fall back to the corresponding `DocumentSourceRow.SourceFileName`; do not fabricate a source name from the final path.
- [ ] Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DocumentArchivingStageIntegrationTests`.

### Task 2: Durable Inventory for Legacy Files Without Artifact Rows

**Files:**
- Add `src/InvoiceFlowAI.Infrastructure/Persistence/Entities/LegacyArchiveInventoryRow.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- Add an EF migration and update `InvoiceFlowDbContextModelSnapshot.cs`
- Add `src/InvoiceFlowAI.Application/Archive/ILegacyArchiveInventoryStore.cs`
- Add `src/InvoiceFlowAI.Infrastructure/Archive/EfLegacyArchiveInventoryStore.cs`
- Modify `src/InvoiceFlowAI.Application/Archive/IArchiveFileSystem.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Archive/PhysicalArchiveFileSystem.cs`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/LegacyArchiveInventoryStoreTests.cs`

- [ ] Add SQLite tests for an unregistered direct child file: it receives a stable opaque inventory ID, its original filename and root-relative path are locally persisted, and no document/artifact row is fabricated.
- [ ] Add tests proving repeated inventory reads return the same ID, a moved item is not re-imported, and same-name/content collisions are represented separately by relative path.
- [ ] Run the new tests red before implementation.
- [ ] Add a direct-child file enumeration operation to `IArchiveFileSystem`; return only files, exclude directories and reparse points, and ensure each returned path resolves under the requested root.
- [ ] Add the durable legacy inventory row/store with explicit states `Discovered`, `Prepared`, `Review`, and `RecoveryRequired`; make the ID deterministic from normalized relative path plus content hash.
- [ ] Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~LegacyArchiveInventoryStoreTests`.

### Task 3: Build the Cross-Run CWT Inventory

**Files:**
- Modify `src/InvoiceFlowAI.Application/Archive/IArchiveArtifactStore.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Archive/EfArchiveArtifactStore.cs`
- Add `src/InvoiceFlowAI.Application/Archive/CwtArchiveInventoryItem.cs`
- Add `src/InvoiceFlowAI.Application/Archive/ICwtArchiveInventory.cs`
- Add `src/InvoiceFlowAI.Infrastructure/Archive/CwtArchiveInventory.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/CwtArchiveInventoryTests.cs`

- [ ] Add a failing test where a prior-run committed hotel artifact, not present in the current candidate batch, is returned with its original source filename, persisted path, hash, document ID, and artifact ID.
- [ ] Add a failing test where a matching direct child file in `住宿发票` has no artifact row and is returned once as a legacy item; nested files and non-matching files are excluded.
- [ ] Run both tests red.
- [ ] Implement `ICwtArchiveInventory.ListAsync(outputRoot, cancellationToken)` to combine all-run committed accommodation artifacts and direct legacy-folder files, deduplicating physical files already represented by artifact rows.
- [ ] Resolve old artifact source names using `DocumentSourceRow.SourceFileName`; use role/document-type evidence to include accommodation artifacts only, and fail closed rather than deriving identity from an archive basename.
- [ ] Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~CwtArchiveInventoryTests`.

### Task 4: Relocate Inventory Items and Recover Failures

**Files:**
- Modify `src/InvoiceFlowAI.Application/Archive/CwtCancellationFinalization.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Archive/CwtCancellationFinalizer.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/Archive/ArchiveRecoveryService.cs`
- Modify `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/CwtCancellationFinalizerTests.cs`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveRecoveryServiceTests.cs`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageIntegrationTests.cs`

- [ ] Add a failing finalizer test proving one cancellation moves matching prior-run artifact rows and unregistered legacy files even when neither item is in the current candidate batch; mismatched filenames remain untouched.
- [ ] Add collision tests: existing review destination means skip without overwriting; duplicate matching names from distinct relative paths get distinct durable IDs and deterministic safe destinations.
- [ ] Add failure tests for sidecar write, database transaction failure after move, and rollback move failure; assert the DB location reflects the actual surviving file, review state is durable when possible, and audit/sidecar contains only opaque IDs, reason, hash, and timestamp.
- [ ] Run these tests red.
- [ ] Refactor `CwtCancellationFinalizer` to match `ICwtArchiveInventory` items rather than requiring a `CandidateProcessResult` for every confirmation; preserve cancellation candidate ordering and Python's direct-child filename rule.
- [ ] Move and hash-check each match; update either the existing artifact row or legacy inventory row in a transaction; record `RecoveryRequired` with the real surviving path if rollback fails.
- [ ] Extend startup recovery to reconcile both existing artifacts and legacy inventory records without deleting user files.
- [ ] Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~CwtCancellationFinalizerTests|FullyQualifiedName~ArchiveRecoveryServiceTests|FullyQualifiedName~DocumentArchivingStageIntegrationTests"`.

### Task 5: Matrix and Regression Validation

**Files:**
- Modify `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`
- Test `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/DesktopParityFixtureTests.cs`

- [ ] Add synthetic assertions for cross-run inventory, legacy unregistered file inventory, filename matching, hash/path preservation, idempotent retry, collision behavior, and recovery after relocation failure.
- [ ] Keep the CWT row `partial` until the source-name fallback and inventory/recovery cases all pass; document that category classification occurs before finalization and that CWT matching itself does not mutate history/category.
- [ ] Run `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Archive|FullyQualifiedName~Pairing|FullyQualifiedName~DesktopParity"`.
- [ ] Run `git diff --check` and inspect `git status --short`; do not commit or force-add ignored plans.
