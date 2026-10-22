# URL Recovery Group and Result Contracts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Group same-message provider URLs, merge expected-field evidence deterministically, capture multiple artifacts, and pass only the selected primary artifact to the existing pipeline.

**Architecture:** Add immutable application contracts for URL groups, field evidence, artifact inventories, and the selected result. Candidate collection builds groups in discovery order and uses HMAC-backed group/artifact identities; recovery stages and clients exchange one group result whose selected artifact follows Python selector behavior, including XML-only direct fallback.

**Tech Stack:** .NET SDK 10.0.401, C# 14, xUnit 2, FluentAssertions, EF Core SQLite, existing `PublicUrlPolicy` and candidate identity key provider.

**Implementation Status (2026-09-24):** Tasks 1-4 are implemented with focused coverage for expected-field evidence, transient grouping/identity, artifact result invariants, and selected-artifact pipeline behavior. Application and full solution tests pass (420 total). Commit steps are intentionally not performed.

## Global Constraints

- `global.json` pins .NET SDK `10.0.401` with `rollForward` set to `disable`.
- Application and Infrastructure target `net10.0`; Infrastructure tests target `net10.0-windows`.
- Package versions remain centrally managed in `Directory.Packages.props`.
- Raw URLs, query parameters, email bodies, artifact bytes, and raw exceptions must not be persisted or logged.
- A recovery group yields at most one main-chain candidate; captured non-selected artifacts remain transient evidence.
- Direct recovery preserves the Python selector's matched-XML and single-non-conflicting-XML fallback when there is no PDF.

---

### Task 1: Preserve expected-field evidence through discovery (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Application/Mail/ExpectedFieldEvidence.cs`
- Modify: `src/InvoiceFlowAI.Application/Mail/MailboxUrlCandidate.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Mail/MailboxUrlCandidateDiscovery.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Mail/MailKitMailboxScannerTests.cs`

**Interfaces:**

- Produces `ExpectedFieldEvidence(string Value, ExpectedFieldSource Source, int DiscoveryOrder)` and `ExpectedFieldSource.UrlQuery`, `Subject`, `Body`.
- `MailboxUrlCandidate.ExpectedFieldEvidence` is an optional read-only map from field name to an ordered evidence list; retain `ExpectedFields` as the legacy winning-value projection for existing consumers.

- [x] **Step 1: Add a failing evidence precedence test**

```csharp
// In MailKitMailboxScannerTests, extend the existing synthetic-message fixture
// with three distinct invoice numbers in URL query, subject, and body. Assert
// that ExpectedFieldEvidence retains all sources and ExpectedFields projects
// the query value, without retaining the raw message body.
```

- [x] **Step 2: Run the focused test and confirm the missing evidence API fails to compile**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~ExtractExpectedFields_marks_query_subject_and_body_evidence`

Expected: FAIL because the evidence type/property is not implemented.

- [x] **Step 3: Implement evidence values without changing normalized legacy fields**

```csharp
public enum ExpectedFieldSource { UrlQuery, Subject, Body }

public sealed record ExpectedFieldEvidence(string Value, ExpectedFieldSource Source, int DiscoveryOrder);
```

Populate evidence for each extracted field; query values outrank subject values, which outrank body values. Keep the existing `ExpectedFields` projection unchanged.

- [x] **Step 4: Run the focused test and the mailbox scanner test class**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --filter FullyQualifiedName~MailKitMailboxScannerTests`

Expected: PASS with no raw body copied into the candidate.

- [ ] **Step 5: Commit the evidence contract and its tests**

```bash
git add src/InvoiceFlowAI.Application/Mail/ExpectedFieldEvidence.cs src/InvoiceFlowAI.Application/Mail/MailboxUrlCandidate.cs src/InvoiceFlowAI.Infrastructure/Mail/MailboxUrlCandidateDiscovery.cs tests/InvoiceFlowAI.Infrastructure.Tests/Mail/MailKitMailboxScannerTests.cs
git commit -m "feat: preserve URL expected-field evidence"
```

### Task 2: Group provider URLs and derive a stable group identity (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Application/Mail/UrlCandidateGroup.cs`
- Modify: `src/InvoiceFlowAI.Application/Candidates/ICandidateIdentityFactory.cs`
- Modify: `src/InvoiceFlowAI.Application/Candidates/CandidateIdentityFactory.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/CandidateCollectionStage.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineContracts.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pipeline/CandidateCollectionStageTests.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Candidates/CandidateIdentityFactoryTests.cs`

**Interfaces:**

- Produces `UrlCandidateGroup(string ProviderFamily, IReadOnlyList<MailboxUrlCandidate> Candidates, IReadOnlyDictionary<string, IReadOnlyList<ExpectedFieldEvidence>> ExpectedFieldEvidence, DocumentIdentity GroupIdentity)`.
- Add `CreateUrlGroupAsync(string providerFamily, IReadOnlyList<MailboxUrlCandidate> candidates, CancellationToken cancellationToken)` to `ICandidateIdentityFactory`. Build the group only after this call returns its opaque `GroupIdentity`.
- A group key is scoped by account, mailbox, UIDVALIDITY, message UID, and provider family. Empty-family candidates remain separate groups.

- [x] **Step 1: Add tests for scope boundaries, discovery order, and identity stability**

```csharp
[Fact]
public async Task Groups_only_same_message_provider_urls_and_preserves_first_seen_order()
{
    var urls = new[]
    {
        Url("message-1", 0, "https://fixture.invalid/a?sig=a"),
        Url("message-1", 1, "https://fixture.invalid/b?sig=b"),
        Url("message-2", 2, "https://fixture.invalid/c?sig=c"),
        Url("message-1", 3, "https://unknown.invalid/d?sig=d") with { ProviderFamily = string.Empty },
    };
    var stage = new CandidateCollectionStage(new FakeIdentityFactory(), new FakeHistoryReader());
    var scan = Scan(
        new[] { Message("message-1"), Message("message-2") },
        Array.Empty<MailboxAttachmentCandidate>(),
        urls);
    var batch = await stage.ExecuteAsync(scan, CancellationToken.None);

    batch.Items.Should().HaveCount(3);
    batch.Items.Select(item => item.SourceUrlGroup!.Candidates.Count).Should().Equal(2, 1, 1);
    batch.Items[0].SourceUrlGroup!.Candidates.Select(item => item.Sequence)
        .Should().Equal(0, 1);
}
```

- [x] **Step 2: Run candidate collection and identity tests to confirm the new contract is absent**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateCollectionStageTests`

Expected: FAIL because URL work items are currently created one-per-link.

- [x] **Step 3: Implement deterministic transient grouping and an HMAC group identity**

```csharp
public sealed record UrlCandidateGroup(
    string ProviderFamily,
    IReadOnlyList<MailboxUrlCandidate> Candidates,
    IReadOnlyDictionary<string, string> ExpectedFields,
    IReadOnlyDictionary<string, IReadOnlyList<ExpectedFieldEvidence>> ExpectedFieldEvidence,
    DocumentIdentity GroupIdentity);
```

Deduplicate exact normalized absolute URIs within the group, merge field evidence by source priority and then discovery order, and derive the group identity using the existing secret key provider. Do not include raw URLs in `DocumentCandidate.Metadata`.

- [x] **Step 4: Run the focused tests and verify unrelated attachments retain ordering**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~CandidateCollectionStageTests`

Expected: PASS; attachment ordering and terminal results remain unchanged.

- [ ] **Step 5: Commit grouping and identity behavior**

```bash
git add src/InvoiceFlowAI.Application/Mail/UrlCandidateGroup.cs src/InvoiceFlowAI.Application/Candidates/ICandidateIdentityFactory.cs src/InvoiceFlowAI.Application/Candidates/CandidateIdentityFactory.cs src/InvoiceFlowAI.Application/Pipeline/CandidateCollectionStage.cs src/InvoiceFlowAI.Application/Pipeline/PipelineContracts.cs tests/InvoiceFlowAI.Application.Tests/Pipeline/CandidateCollectionStageTests.cs tests/InvoiceFlowAI.Application.Tests/Candidates/CandidateIdentityFactoryTests.cs
git commit -m "feat: group provider URL candidates"
```

### Task 3: Define captured-artifact and primary-selection results (Complete)

**Files:**

- Create: `src/InvoiceFlowAI.Application/Url/RecoveredArtifactKind.cs`
- Create: `src/InvoiceFlowAI.Application/Url/CapturedUrlArtifact.cs`
- Create: `src/InvoiceFlowAI.Application/Url/UrlRecoveryResult.cs`
- Modify: `src/InvoiceFlowAI.Application/Url/IUrlRecoveryClient.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Url/UrlRecoveryResultTests.cs`

**Interfaces:**

- `RecoveredArtifactKind` contains only `Pdf`, `Xml`, and `Ofd`.
- `CapturedUrlArtifact` carries kind, MIME type, content, source ordinal, normalized invoice fields, and safe selection evidence.
- `UrlRecoveryResult(IReadOnlyList<CapturedUrlArtifact> Artifacts, int? SelectedArtifactIndex)` contains all accepted captures, at most one selected artifact, and a computed nullable `SelectedArtifact` property.
- The selected index must be in range and identify the one primary output; `null` means selector failure and must be paired with `UrlRecoveryException` at the client boundary.

- [x] **Step 1: Add result invariant tests**

```csharp
[Fact]
public void Selected_artifact_index_must_reference_an_accepted_capture()
{
    var pdf = new CapturedUrlArtifact(
        RecoveredArtifactKind.Pdf, "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nfixture"), 0,
        "0".PadLeft(64, '0'), "https://files.fixture.invalid",
        new Dictionary<string, string>(), null, "NO_EXPECTED_FIELDS");
    var act = () => new UrlRecoveryResult([pdf], 1);

    act.Should().Throw<ArgumentOutOfRangeException>();
}
```

- [x] **Step 2: Run the focused test and confirm the result contract is missing**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~UrlRecoveryResultTests`

Expected: FAIL because the result and artifact types do not exist.

- [x] **Step 3: Implement immutable result types and validate selection invariants**

```csharp
public sealed record CapturedUrlArtifact(
    RecoveredArtifactKind Kind,
    string ContentType,
    ReadOnlyMemory<byte> Content,
    int SourceUrlOrdinal,
    string Sha256,
    string? SanitizedResolvedOrigin,
    IReadOnlyDictionary<string, string> InvoiceFields,
    bool? ExpectedMatch,
    string MatchReasonCode);
```

Keep raw URLs, response headers containing capability values, and response excerpts out of the result type.

- [x] **Step 4: Run result contract tests**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~UrlRecoveryResultTests`

Expected: PASS for empty, selected, and invalid-index cases.

- [ ] **Step 5: Commit the result contract**

```bash
git add src/InvoiceFlowAI.Application/Url/RecoveredArtifactKind.cs src/InvoiceFlowAI.Application/Url/CapturedUrlArtifact.cs src/InvoiceFlowAI.Application/Url/UrlRecoveryResult.cs src/InvoiceFlowAI.Application/Url/IUrlRecoveryClient.cs tests/InvoiceFlowAI.Application.Tests/Url/UrlRecoveryResultTests.cs
git commit -m "feat: define URL recovery artifact result"
```

### Task 4: Apply selector output to the existing single-item pipeline (Complete)

**Files:**

- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineContracts.cs`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/UrlRecoveryStage.cs`
- Modify: `src/InvoiceFlowAI.Application/Candidates/ICandidateIdentityFactory.cs`
- Modify: `src/InvoiceFlowAI.Application/Candidates/CandidateIdentityFactory.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Pipeline/UrlRecoveryStageTests.cs`

**Interfaces:**

- `CandidateWorkItem` carries an optional `UrlCandidateGroup` instead of a single URL source for grouped recovery.
- Add `CreateRecoveredArtifactAsync(DocumentIdentity sourceGroupIdentity, RecoveredArtifactKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)` to `ICandidateIdentityFactory`.
- `UrlRecoveryStage` emits one work item with selected content and selected format; all non-selected captures remain in the result evidence and do not become work items.

- [x] **Step 1: Add tests for a selected PDF plus XML evidence and for XML-only fallback**

```csharp
var selectedPdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nfixture");
var selectedPdf = new CapturedUrlArtifact(
    RecoveredArtifactKind.Pdf, "application/pdf", selectedPdfBytes, 0,
    Convert.ToHexString(SHA256.HashData(selectedPdfBytes)).ToLowerInvariant(), "https://files.fixture.invalid",
    new Dictionary<string, string>(), true, "EXPECTED_FIELDS_MATCH");
var diagnosticXmlBytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />");
var diagnosticXml = new CapturedUrlArtifact(
    RecoveredArtifactKind.Xml, "application/xml", diagnosticXmlBytes, 0,
    Convert.ToHexString(SHA256.HashData(diagnosticXmlBytes)).ToLowerInvariant(), "https://files.fixture.invalid",
    new Dictionary<string, string>(), null, "ARTIFACT_NOT_SELECTED");
var recoveryResult = new UrlRecoveryResult([selectedPdf, diagnosticXml], 0);
// Configure the grouped recovery fake with recoveryResult, then assert the
// stage emits exactly one item, with selectedPdf bytes and a .pdf filename.
```

- [x] **Step 2: Run the focused test and confirm the stage still consumes the old single-result API**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~UrlRecoveryStageTests`

Expected: FAIL until the stage consumes `UrlRecoveryResult` and group inputs.

- [x] **Step 3: Implement selected-artifact application and deterministic artifact identity**

```csharp
var selected = result.SelectedArtifact;
var identity = await _identityFactory.CreateRecoveredArtifactAsync(
    item.SourceUrlGroup!.GroupIdentity, selected.Kind, selected.Content, cancellationToken);
```

Use the existing candidate key provider for a versioned HMAC identity over source-group identity, kind, and SHA-256 content digest. Preserve the source group identity as the history lookup key; persistence of its relationship to the archived artifact is wired in the pipeline integration plan.

- [x] **Step 4: Run URL stage, identity, and EF history focused tests**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~UrlRecoveryStageTests`

Expected: PASS; no non-selected capture becomes a candidate, and content filename/MIME match the selected artifact.

- [ ] **Step 5: Commit selected-result pipeline behavior**

```bash
git add src/InvoiceFlowAI.Application/Pipeline/PipelineContracts.cs src/InvoiceFlowAI.Application/Pipeline/UrlRecoveryStage.cs src/InvoiceFlowAI.Application/Candidates/ICandidateIdentityFactory.cs src/InvoiceFlowAI.Application/Candidates/CandidateIdentityFactory.cs tests/InvoiceFlowAI.Application.Tests/Pipeline/UrlRecoveryStageTests.cs
git commit -m "feat: pass selected URL artifact to pipeline"
```

## Final Verification

Run `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj` and `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`. Both must pass before the provider strategy plan begins.
