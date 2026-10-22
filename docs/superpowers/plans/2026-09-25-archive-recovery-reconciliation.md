# Archive Recovery Reconciliation Implementation Plan

> **For agentic workers:** Execute each task with a focused failing test first, then the smallest implementation change. Do not commit; the user explicitly requested no commits.

**Goal:** Prove archive recovery hash invariants and pairing reconciliation across successful and fail-closed recovery states.

**Architecture:** Exercise `ArchiveRecoveryService` through its existing in-memory store/filesystem/unit-of-work/audit/pairing fakes. Keep file-state decisions in the service and assert the exact snapshot state passed to pairing reconciliation after every resolution.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, `IArchiveRecoveryService` abstractions.

## Global Constraints

- Use synthetic bytes and paths only.
- Recovery must never silently delete user files or commit mismatching content.
- Preserve stable safe reason codes and no raw local paths in user-facing messages.
- Do not commit.

---

### Task 1: Hash and Audit Invariants

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveRecoveryServiceTests.cs`
- Modify only if a test fails for a production defect: `src/InvoiceFlowAI.Application/Archive/ArchiveRecoveryService.cs`

- [ ] Add a temp-only hash-mismatch case; assert `RecoveryRequired`, `ARCHIVE_TEMP_HASH_MISMATCH`, no final file creation, no commit, and unchanged temp bytes.
- [ ] Strengthen final-match and temp-restore cases to assert artifact snapshot state, expected content hash, audit event count/type, and transaction commit count.
- [ ] Run `ArchiveRecoveryServiceTests` and verify the mismatch test fails if any path currently commits or mutates bytes incorrectly.
- [ ] Fix only defects exposed by these assertions; re-run the same test class.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArchiveRecoveryServiceTests`
Expected: all selected tests pass.

### Task 2: Pair-State Reconciliation Matrix

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveRecoveryServiceTests.cs`
- Modify if required: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfPairingStore.cs` and/or `src/InvoiceFlowAI.Application/Archive/ArchiveRecoveryService.cs`

- [ ] Parameterize recovery outcomes as `Committed` for matching final/temp bytes and `RecoveryRequired` for both files missing, final mismatch, and temp mismatch.
- [ ] Assert `ReconcileArchiveStateAsync` receives exactly the post-resolution artifact snapshots for each row and is called after the artifact-store update.
- [ ] Assert a recovered pair becomes committed only when all required artifacts are committed; any recovery-required artifact leaves the pair recovery-required.
- [ ] Run the recovery service test class and the focused `EfPairingStoreTests`; repair only failures demonstrated by the test evidence.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArchiveRecoveryServiceTests|FullyQualifiedName~EfPairingStoreTests`
Expected: all selected tests pass.

### Task 3: Recovery Fixture and Matrix

**Files:**
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity/archive-recovery.json` (create)
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/DesktopParityFixtureTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`

- [ ] Create a sanitized fixture with cases `final_match`, `temp_match`, `final_hash_mismatch`, `temp_hash_mismatch`, and `both_missing`; include only synthetic ids, content labels, expected state, and reason code.
- [ ] Add one fixture-driven test that executes the real recovery service with fakes and compares the resulting state/reason/audit/pairing snapshot for each fixture row.
- [ ] Run the fixture test red, implement only any service gap it reveals, and re-run it.
- [ ] Update the archive matrix row to `verified` only if every listed recovery and reconciliation invariant is asserted; otherwise retain `partial` with the uncovered branch.

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArchiveRecoveryServiceTests|FullyQualifiedName~DesktopParityFixtureTests`
Expected: all selected tests pass.
