# Desktop Python-to-C# Parity Gaps Implementation Plan

> **For agentic workers:** Implement inline in this session. Keep each subsystem independently testable and do not commit changes unless requested.

**Goal:** Close the approved, decision-free parity test gaps using shared sanitized fixtures and deterministic fakes, fixing only confirmed Python/C# behavior differences.

**Architecture:** Reuse `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity` for cross-language golden inputs. Keep strategy and lifecycle tests at their existing C# owner layers, with fake transports/stores and no real mailbox, provider, or network. Leave unresolved architectural decisions recorded in the matrix rather than encoding assumptions as behavior.

**Tech Stack:** .NET 10, xUnit/FluentAssertions, Python 3.14 pytest, JSON synthetic fixtures.

## Global Constraints

- No real mailbox data, credentials, signed query strings, or external network calls in fixtures/tests.
- Python reference behavior is the parity target except rows explicitly marked decision-needed.
- Tests first: prove each new assertion fails for the missing coverage or behavior, then implement the smallest fix.
- Update the parity matrix only after the corresponding focused test passes.
- Do not change IMAP recursive split/SEARCH batching, proxy mode, or concurrency policy without a separate product decision.

### Batch 1: Pairing Golden Edges

**Files:** `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity/pairing.json`, `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/DesktopParityFixtureTests.cs`, `tests/test_pairing_engine.py`.

- Add provider mismatch and merchant/UID tie-break cases to the shared fixture.
- Ensure Python and C# consume the same expected membership and ambiguity results.
- Run focused Python pairing tests and the C# DesktopParity fixture test.

### Batch 2: Provider Recovery

**Files:** `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/DesktopParity/provider-recovery.json`, `tests/InvoiceFlowAI.Infrastructure.Tests/Url/NuonuoScanRecoveryStrategyTests.cs`, `tests/InvoiceFlowAI.Infrastructure.Tests/Url/BaiwangRecoveryStrategyTests.cs`, `tests/InvoiceFlowAI.Infrastructure.Tests/Url/DirectArtifactProbeTests.cs`, `tests/InvoiceFlowAI.Infrastructure.Tests/Url/DirectInvoiceArtifactSelectorTests.cs`, `tests/test_provider_url_recovery.py`.

- Cover Nuonuo expected-field projection and retry orchestration.
- Cover Baiwang OFD handoff and requested-format download.
- Cover each direct family with malformed artifact and XML/OFD selection cases where the Python reference defines the outcome.
- Use fake responses only; no live provider requests.

### Batch 3: Acceptance Through Archive Completion

**Files:** `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/Fixtures` (new sanitized fixture), `tests/InvoiceFlowAI.Infrastructure.Tests/Pipeline/DocumentExtractionStageTests.cs`, `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageTests.cs`, `tests/test_task8_review_remediation.py`.

- Assert rejected provider output is retained, never sent to archive commit, and leaves the run incomplete/unresolved.
- Keep the assertion at the pipeline/archive boundary, not only in the acceptance unit test.

### Batch 4: Archive Recovery State Matrix

**Files:** `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveRecoveryServiceTests.cs`, relevant `PairingStoreTests.cs`, shared DesktopParity recovery fixture, and Python archive recovery tests.

- Cover final present, temp present, source/final missing, and hash mismatch.
- Assert artifact state, audit state, and pair reconciliation for every fixture case.

### Batch 5: Candidate Identity and Privacy

**Files:** `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/DesktopParityFixtureTests.cs`, `tests/InvoiceFlowAI.Application.Tests/Pipeline/CandidateCollectionStageTests.cs`, Python candidate identity tests.

- A shared fixture asserts stable identity across input order, opaque URL-derived identity, and malformed candidate manual-review retention.
- Assert raw URL/query secrets are absent from persisted/test-visible payloads.

### Batch 6: IMAP Session Date Boundaries

**Files:** `tests/InvoiceFlowAI.Infrastructure.Tests/Email/MailKitMailboxSessionTests.cs` (or existing session test owner), synthetic mailbox fixture, Python mailbox scanner tests.

- With a fake IMAP session, compare the summary UID request, Shanghai-local date filter, and full-message fetch UID set at inclusive/exclusive boundaries and for unknown dates.
- Do not port Python batch-retry or strict SEARCH parser semantics; those remain decision-needed.

### Batch 7: Progress and Terminal Ordering

**Files:** `tests/InvoiceFlowAI.Application.Tests/Runs/RunExecutionServiceTests.cs`, `tests/InvoiceFlowAI.Application.Tests/Runs/DesktopRunServiceTests.cs`, Python processing-pipeline tests.

- Use deterministic barriers to assert no progress/trace event is emitted after terminal completion.
- Assert total progress remains stable for a non-empty multi-candidate run.

### Batch 8: XML Parser Layouts and Partial Results

**Files:** `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/XmlInvoiceParserTests.cs`, shared DesktopParity extraction fixture, Python XML extraction tests.

- Add representative supported XML layouts and explicit missing/partial-field outcomes.
- Compare normalized fields and failure/missing-field categories, not parser internals.

### Final Verification

- Run each focused Python and C# test set after its batch.
- Run the complete affected C# test projects and the focused Python parity/provider/archive/mailbox/pairing/XML tests.
- Review `git diff --check`, update only matrix rows proven by fixtures, and report unresolved decision-needed rows separately.
