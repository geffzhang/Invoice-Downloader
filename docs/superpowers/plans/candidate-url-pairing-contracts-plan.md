# Candidate, URL, and Pairing Contracts Implementation Plan

> **For agentic workers:** Use the `superpowers:executing-plans` skill to implement this plan task-by-task. Keep the later candidate/pairing and URL runtime plans separate from this contract-only slice.

**Goal:** Define the mailbox URL-candidate DTO and pin the agreed Python-parity and pairing expectations in a synthetic, versioned fixture corpus without activating URL recovery at runtime.

**Architecture:** Extend the Application mailbox scan contract with a run-scoped URL candidate type that contains no raw message body. Add a fixture-driven contract test in the existing Application test project. Python-derived expected behavior is data only in this slice; executable extraction, identity persistence, providers, workers, and pairing orchestration belong to later plans.

**Tech Stack:** .NET 10, C# records, `System.Text.Json`, xUnit, FluentAssertions, checked-in JSON fixtures.

## Global Constraints

- Keep `global.json` pinned to .NET SDK `10.0.401`.
- Do not add a production dependency on Python.
- Do not include raw email bodies, real URLs, query tokens, credentials, or real mailbox data in fixtures or logs.
- Preserve existing `MailboxScanResult` construction call sites by adding the new collection as an optional trailing contract member.
- Do not change `PairingEngine` or activate URL recovery in the pipeline in this slice.

---

### Task 1: Add the run-scoped mailbox URL candidate contract

**Files:**

- Create: `src/InvoiceFlowAI.Application/Mail/MailboxUrlCandidate.cs`
- Modify: `src/InvoiceFlowAI.Application/Mail/MailboxScanner.cs`
- Test: `tests/InvoiceFlowAI.Application.Tests/Mail/MailboxUrlCandidateContractTests.cs`

**Interfaces:**

- Consumes: existing `MailboxScanResult`, `Uri`, and `MailboxAttachmentCandidate` definitions.
- Produces: `MailboxUrlCandidate(string AccountId, string Mailbox, string UidValidity, string MessageUid, Uri SourceUrl, string ProviderFamily, string ProviderGroupId, IReadOnlyDictionary<string, string> ExpectedFields, long Sequence)`.
- `MailboxScanResult` keeps its existing five positional constructor parameters and adds `public IReadOnlyList<MailboxUrlCandidate> UrlCandidates { get; init; } = Array.Empty<MailboxUrlCandidate>();` in its record body.

- [ ] **Step 1: Write the failing contract test**

Add tests that instantiate a `MailboxUrlCandidate`, verify each property retains its supplied value including `SourceUrl.Query`, verify `MailboxScanResult` defaults URL candidates to an empty list, and verify existing five-argument `MailboxScanResult` construction remains valid. Set a non-empty candidate list through a `with { UrlCandidates = candidates }` expression.

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~MailboxUrlCandidateContractTests`

Expected: FAIL because `MailboxUrlCandidate` and `MailboxScanResult.UrlCandidates` do not exist yet.

- [ ] **Step 3: Add the minimal contract**

Create the immutable URL-candidate record with the exact signature listed above. Add the init-only `UrlCandidates` record-body property with `Array.Empty<MailboxUrlCandidate>()` as its default. Preserve all existing five-argument `MailboxScanResult` constructor call sites and keep the source URI run-scoped. Do not add raw body fields or a URL-to-string persistence property.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~MailboxUrlCandidateContractTests`

Expected: PASS with existing mailbox scanner contract tests unaffected.

### Task 2: Add synthetic Python golden fixtures and a schema test

**Files:**

- Create: `tests/InvoiceFlowAI.Application.Tests/Fixtures/CandidateUrlPairing/python-golden.v1.json`
- Modify: `tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj`
- Test: `tests/InvoiceFlowAI.Application.Tests/Mail/MailboxUrlCandidateContractTests.cs`

**Interfaces:**

- Consumes: Task 1 `MailboxUrlCandidate` and the Python reference behavior in `email_fetcher.py`, `provider_direct_invoice.py`, `archive_pairing.py`, and `pairing_engine.py`.
- Produces: a JSON object with `schemaVersion: 1` and `cases`; every case has `id`, `kind`, `input`, and `expected`. Kinds are `link_extraction`, `provider_fields`, `provider_group`, `pairing_projection`, and `pairing_outcome`. Inputs contain only fabricated mail text/HTML/URLs or fabricated invoice values. Expected outputs contain ordered fixture IDs, provider family, the four allowed expected-field keys, opaque group IDs, role names, and outcome names.

- [ ] **Step 1: Write the failing fixture test**

Add a test that loads `Fixtures/CandidateUrlPairing/python-golden.v1.json`, asserts `schemaVersion` equals `1`, asserts the corpus includes plain-text and HTML links, duplicate-link ordering, all seven direct provider family names plus Baiwang, all four pairable roles, a non-pairable type, and `matched`, `unmatched`, and `ambiguous` outcomes. Assert fixture property names do not include raw body, credential, authorization, or query-token fields.

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~MailboxUrlCandidateContractTests`

Expected: FAIL because the fixture does not exist.

- [ ] **Step 3: Add the fixture and copy rule**

Populate the fixture only with fabricated URLs and identifiers. Preserve query parameters only in fabricated input examples; expected outputs contain only provider family, permitted normalized expected fields, opaque group IDs, ordered fixture IDs, role, and outcome. Add a project item that copies JSON fixtures to the test output directory with `PreserveNewest`.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~MailboxUrlCandidateContractTests`

Expected: PASS and the test discovers the fixture from both the project directory and test output directory.

### Task 3: Validate the contract-only boundary

**Files:**

- Test: `tests/InvoiceFlowAI.Application.Tests/Mail/MailboxUrlCandidateContractTests.cs`
- Validate: `tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj`

- [ ] **Step 1: Add a compatibility assertion**

Assert that existing `MailboxScanResult` fields retain their names and values when URL candidates are empty, and that candidate expected fields contain only `invoice_number`, `seller`, `invoice_date`, and `preferred_kind` in the contract fixture.

- [ ] **Step 2: Run the contract test**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --filter FullyQualifiedName~MailboxUrlCandidateContractTests`

Expected: PASS.

- [ ] **Step 3: Run the Application test project**

Run: `dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj`

Expected: all Application tests pass; no pipeline stage registration or runtime recovery behavior has changed.

## Handoff

After this plan is reviewed, create separate detailed plans for candidate collection/pairing orchestration and native URL recovery. Do not expand this contract-only slice into scanner extraction, HMAC identity persistence, provider networking, Playwright execution, or archive behavior.
