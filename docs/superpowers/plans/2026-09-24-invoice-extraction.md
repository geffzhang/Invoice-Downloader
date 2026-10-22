# Native Invoice Extraction Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with `superpowers:subagent-driven-development` or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax for tracking. Do not commit changes unless the user explicitly requests a commit.

**Goal:** Deliver the migration-design-compliant native extraction route for XML, OFD, PDF text, deterministic/special parsers, local OCR, DeepSeek Track A/B, acceptance, and candidate-level outcomes.

**Architecture:** Keep route selection and typed outcomes in Application, domain invoice types in Domain, and all PDF/OCR/AI SDK calls in Infrastructure. Format-deterministic parsing precedes registered special parsers; only unresolved documents enter bounded OCR/AI fallback. Every route preserves candidate identity and returns through normalization, acceptance, and the existing candidate-result pipeline.

**Tech Stack:** .NET 10 (`10.0.401`), C#; PdfPig; PDFiumCore; SkiaSharp; Sdcb.SimdPaddleOCR; Microsoft.Extensions.AI / OpenAI-compatible DeepSeek; xUnit; FluentAssertions.

## Global Constraints

- Use the repository-pinned .NET SDK `10.0.401`; `global.json` disables roll-forward.
- Target the existing `net10.0` application projects and `net10.0-windows` x64 Infrastructure test project.
- Keep PDF/OCR/AI provider SDK and HTTP types in `InvoiceFlowAI.Infrastructure`; Application and Domain contracts remain provider-neutral.
- Follow the migration design: render at most 2 pages; default render bounds are 200 DPI, 4096 px width, and 8192 px height; respect configured per-image/request byte limits and existing hard concurrency limits.
- OCR/AI payloads are transient. Never persist or log source bytes, OCR text, Base64, prompt text, full provider responses, API keys, or absolute temporary paths.
- Candidate-level extraction failures are result data, not batch exceptions. Caller cancellation is not a retryable failure.
- Python is a source for deterministic rule fixtures; the migration design takes precedence for route order, DeepSeek/PaddleOCR providers, and fallback triggers.
- Do not add packages unless repository package references prove a required library is missing; current Infrastructure already references PdfPig, PDFiumCore, SkiaSharp, SimdPaddleOCR, Microsoft.Extensions.AI, and Microsoft.Extensions.AI.OpenAI.

---

## File Map

Application contracts and orchestration:

- `src/InvoiceFlowAI.Application/Parsers/IParser.cs`: add explicit parser match/claim semantics without leaking provider types.
- `src/InvoiceFlowAI.Application/Parsers/ParserPipeline.cs`: resolve special-parser matches, priority, same-priority conflicts, claimed-parser failures, and explicit generic-fallback disposition.
- `src/InvoiceFlowAI.Application/Extraction/`: add shared document/render/OCR DTOs, extraction request/result/route/trace contracts, normalizer, acceptance service, and Track A/B orchestrator.
- `src/InvoiceFlowAI.Application/Ai/IChatCompletionService.cs`: extend vision completion to ordered image parts.
- `src/InvoiceFlowAI.Application/Email/EmailBodyReceiptService.cs`: make accepted deterministic receipt results avoid unnecessary AI and remove content from trace.
- `src/InvoiceFlowAI.Application/Pipeline/`: add concrete extraction stage implementing the existing `IDocumentExtractionStage` contract.

Infrastructure implementations:

- `src/InvoiceFlowAI.Infrastructure/Parsers/`: production XML, OFD, PDF text, segmentation, deterministic field and special-case parser implementations. Keep `FixtureBackedParser.cs` test-only in purpose; do not extend it into production parsing.
- `src/InvoiceFlowAI.Infrastructure/Ocr/`: PDF rendering, image preprocessing, and SimdPaddleOCR adapter.
- `src/InvoiceFlowAI.Infrastructure/Ai/DeepSeekChatCompletionService.cs`: production text and multi-image DeepSeek completion, bounded retries, and stable error mapping.
- `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`: register production extraction adapters and stage dependencies.
- `build/model-manifest.ps1`, `build/windows/publish_dotnet_app.ps1`, and the existing release-manifest verifier/tests: package and verify pinned OCR model assets.

Tests:

- `tests/InvoiceFlowAI.Application.Tests/Parsers/`: parser match, priority, conflict, and claimed-failure behavior.
- `tests/InvoiceFlowAI.Application.Tests/Extraction/`: normalization, acceptance, Track A/B decisions, candidate identity, and redaction.
- `tests/InvoiceFlowAI.Application.Tests/Email/EmailBodyReceiptServiceTests.cs`: deterministic receipt behavior and trace privacy.
- `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/`: real parser behavior with sanitized fixtures.
- `tests/InvoiceFlowAI.Infrastructure.Tests/Ocr/`: render/preprocess/OCR adapters and resource bounds.
- `tests/InvoiceFlowAI.Infrastructure.Tests/Ai/DeepSeekChatCompletionServiceTests.cs`: request shape, response parsing, errors, retries, cancellation, and limits.
- `tests/InvoiceFlowAI.Infrastructure.Tests/Pipeline/`: extraction stage candidate isolation and DI composition.

## Task 1: Make Parser Claims and Conflicts Explicit

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Parsers/IParser.cs`
- Modify: `src/InvoiceFlowAI.Application/Parsers/ParserPipeline.cs`
- Modify: `src/InvoiceFlowAI.Application/Parsers/ParserRegistry.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Parsers/FixtureBackedParser.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/ParserPipelineTests.cs`
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/ParserPipelineRoutingTests.cs`

**Interfaces:**
- Add `bool CanParse(ParserWorkItem workItem)` to `IParser`; `false` means not applicable, `true` claims the document.
- Add `ParserOutcomeDisposition` with `Resolved`, `NeedsFallback`, and `Failed`. `NeedsFallback` is the only parser result that permits the generic OCR/AI route; a claimed `Failed` result is terminal for parser dispatch.
- `ParserPipeline` is the special-parser dispatcher. It considers only source-kind-compatible parsers whose `CanParse` returns true, sorts by `Priority DESC, ParserId ASC`, and returns a structured no-match outcome when none claim.
- Two or more claiming parsers at the same highest priority return `SPECIAL_PARSER_CONFLICT`. A unique highest-priority claimed parser runs once; its disposition controls whether extraction resolved, needs generic fallback, or failed.

- [x] **Step 1: Add failing routing tests** for non-match, claimed-parser failure, lower-priority non-execution, same-priority conflict, and deterministic parser ordering. Use fakes that record `CanParse` and `ParseAsync` calls.
- [x] **Step 2: Run the focused test**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~ParserPipelineRoutingTests
```

Expected: new tests fail because `IParser` has no match contract and the current pipeline runs every parser.

- [x] **Step 3: Implement the claim/disposition contract** and change special-parser dispatch to select matching parsers before parsing. Preserve the existing candidate failure shape and make fallback an explicit `NeedsFallback` disposition.
- [x] **Step 4: Update fixture-backed parser tests** to define match behavior explicitly; do not infer a positive claim solely from a fixture returning an invoice.
- [x] **Step 5: Re-run focused parser tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~ParserPipeline
```

Expected: routing and existing fixture parser tests pass; a claimed failure never invokes another parser.

## Task 2: Add Normalization and Acceptance Contracts

**Files:**
- Create: `src/InvoiceFlowAI.Application/Extraction/ExtractionContracts.cs`
- Create: `src/InvoiceFlowAI.Application/Extraction/InvoiceNormalizer.cs`
- Create: `src/InvoiceFlowAI.Application/Extraction/InvoiceAcceptanceService.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Extraction/InvoiceNormalizerTests.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Extraction/InvoiceAcceptanceServiceTests.cs`

**Interfaces:**
- `ExtractionRoute`: `LocalFastPath`, `OcrText`, `VisionFallback`.
- `ExtractionTrace`: route, Track A/B status, reason code, duration, model, input kind, optional response fingerprint; no source content.
- Define the shared `DocumentSource`, `BoundingBox`, `OcrLine`, `RenderedPage`, and `PdfRenderOptions` DTOs in `ExtractionContracts.cs`; these use immutable metadata and transient `ReadOnlyMemory<byte>` image data.
- `InvoiceNormalizer.Normalize(InvoiceDocument input, DocumentIdentity identity)` returns a normalized invoice preserving the candidate identity and uses `decimal`/`DateOnly` conversions only.
- `InvoiceAcceptanceService.Accept(InvoiceDocument invoice, InvoiceAcceptanceOptions options)` returns a typed accepted/manual-review/rejected result with stable reason codes; it does not call network, persistence, or file I/O.

- [x] **Step 1: Add failing tests** for date/amount normalization, red-letter negative amounts, invalid amount/date/type, missing required invoice fields, identity mismatch, and confidence/manual-review mapping.
- [x] **Step 2: Run focused tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~InvoiceNormalizerTests
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~InvoiceAcceptanceServiceTests
```

Expected: tests fail because the production normalizer and acceptance service do not exist.

- [x] **Step 3: Implement pure normalization and acceptance** in Application using the existing Domain invoice types. Reuse stable codes from the migration design, including `INVOICE_DATE_INVALID`, `INVOICE_AMOUNT_INVALID`, `INVOICE_SELLER_MISSING`, `INVOICE_NUMBER_MISSING`, `DOCUMENT_TYPE_UNKNOWN`, `ACCEPTANCE_LOW_CONFIDENCE`, and `IDENTITY_MISMATCH`.
- [x] **Step 4: Run both focused test classes** and verify every outcome preserves the original `DocumentIdentity`.

## Task 3: Implement XML and OFD Deterministic Parsers

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/XmlInvoiceParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/OfdInvoiceParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/OfdPackageReader.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/XmlInvoiceParserTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/OfdInvoiceParserTests.cs`
- Add sanitized fixtures under: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/Parsers/Xml/` and `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/Parsers/Ofd/`

**Interfaces:**
- Both parsers implement `IParser` and use `ParserWorkItem.DocumentBytes`; neither accesses a filesystem path or provider SDK.
- XML supports the `Header`/`EInvoiceData` and legacy `InvoiceInfo`/`BuyerInfo`/`SellerInfo` forms described in the migration design.
- OFD package reader returns bounded, normalized package entries and safely resolves `original_invoice.xml`, Tag/CustomTag, and `Page_*/Content.xml` references.

- [x] **Step 1: Add failing sanitized-fixture tests** for each XML layout, missing invoice number, malformed XML, DTD/external entity rejection, OFD original XML, tags, page content/ObjectRef, multi-page traversal, duplicate IDs, malformed ZIP, and unresolved conflicting fields.
- [x] **Step 2: Run focused tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~XmlInvoiceParserTests
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~OfdInvoiceParserTests
```

Expected: tests fail because production XML/OFD parsers do not exist.

- [x] **Step 3: Implement secure bounded parsing**. Disable DTD and external resolution; cap archive entry count, expanded bytes, XML depth, text length, and page count from migration-design settings. Return stable parser outcomes instead of throwing candidate-level parse failures.
- [x] **Step 4: Run both parser suites** and verify accepted XML/OFD fixtures do not request rendering or AI.

## Task 4: Implement PDF Text Extraction, Segmentation, and Deterministic Rules

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/PdfTextExtractor.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/PdfInvoiceSegmenter.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/PdfFieldParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/PdfInvoiceParser.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/PdfInvoiceParserTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/Parsers/Pdf/`

**Interfaces:**
- `PdfTextExtractor.ExtractPagesAsync(ReadOnlyMemory<byte> pdf, CancellationToken)` returns page-numbered text without exposing PdfPig types.
- `PdfInvoiceSegmenter.Segment(IReadOnlyList<PdfPageText>)` returns stable ordered invoice/document segments.
- `PdfInvoiceParser` attempts useful embedded text and deterministic fields before rendering/OCR; it returns unresolved state to the extraction router when text quality is insufficient.

**Verified boundary:** `ParserOutcome` currently carries one invoice. The PDF parser segments in order but sends documents with multiple invoice markers to `NeedsFallback` (`PDF_MULTIPLE_INVOICES`) instead of silently returning only the first invoice. Full multi-invoice candidate output needs a later application-contract change.

**Progress:** Searchable PDF extraction, bounded page/text/input handling, stable ordered segmentation, common bilingual fields, red-letter amounts, legacy invoice code, `Issue Date`, `数电发票` classification, and redundant seller-label cleanup are implemented and tested. Full standard China e-invoice v2/legacy reconstruction is still incomplete; this task is not marked complete.

- [ ] **Step 1: Add failing tests** for searchable PDF success, scanned/empty text, malformed PDFs, repeated invoice markers, multi-invoice/page-boundary segmentation, date/amount cleanup, seller cleanup, and standard Chinese e-invoice v2/legacy rules.
- [ ] **Step 2: Run the focused parser test**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~PdfInvoiceParserTests
```

Expected: tests fail because production PDF text parsing/segmentation is not implemented.

- [ ] **Step 3: Implement the PdfPig adapter, segmenter, and deterministic parser** with stable page order and no unbounded document buffering.
- [ ] **Step 4: Run focused tests** and assert accepted embedded-text invoices do not call the OCR or AI abstractions.

## Task 5: Migrate Special-Case Parsers and Registry Metadata

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/RailwayTicketParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/AccommodationFolioParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/ForeignInvoiceParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/CitsGbtParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/RideItineraryParser.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Parsers/DidiInvoiceParser.cs`
- Modify: parser registry metadata fixture and `src/InvoiceFlowAI.Application/Parsers/ParserRegistry.cs`
- Modify: `docs/superpowers/fixtures/parsers/registry.v1.json`
- Modify: `tests/InvoiceFlowAI.Contracts.Tests/ContractRoundTripTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Parsers/SpecialInvoiceParserTests.cs`
- Add sanitized fixtures under: `tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/Parsers/Special/`

**Interfaces:**
- Every parser has stable ID/version/priority/source kind/failure code and a deterministic `CanParse` predicate.
- Railway, folio, foreign, CITS/GBT, ride itinerary, and Didi rules consume extracted PDF text and return partial or complete normalized invoice fields.
- Didi is selected before generic OCR/AI; it is not a post-Track-B rescue path.

**Progress:** `RideItineraryParser` (priority 470), `DidiInvoiceParser` (460), `RailwayTicketParser` (450), `AccommodationFolioParser` (440), `CitsGbtParser` (430), and `ForeignInvoiceParser` (420) have focused tests and are registered through Infrastructure DI. The parser registry fixture and sanitized parser metadata fixtures list the same IDs/priorities. Railway uses an unlabeled date preceding the ticket title as departure date; folio prefers checkout date and payment total; Didi requires provider plus transport evidence and prefers tax-inclusive total; CITS currently handles SCCT invoices; foreign invoices currently handle searchable USD PDF layouts; ride itinerary currently handles AMAP/Didi platform markers, trip start date, and total. This is not full parser parity: CITS hotel/itinerary variants, broader foreign currencies/layouts, legacy folio date variants, provider parser coverage, and registry fingerprint generation remain open.

- [ ] **Step 1: Add fixture-driven failing tests** for positive match, non-match, claimed parse failure, deterministic override precedence, folio `DD/MM/YY`, railway departure/date correction, negative amounts, and Didi-before-AI routing.
- [ ] **Step 2: Run the focused special-parser test**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~SpecialInvoiceParserTests
```

Expected: tests fail because production special parsers and their match predicates do not exist.

- [ ] **Step 3: Implement individual parsers** using sanitized golden cases derived from `tests/test_invoice_regression_p2.py` and the approved migration fixtures. Do not port the monolithic Python LLM router.
- [ ] **Step 4: Add registry fingerprint/order tests** and run parser routing plus special parser tests. Assert only equal-priority multiple matches conflict; a higher-priority claimed parser is not silently replaced after failure.

## Task 6: Make Email-Body Receipt Deterministic and Privacy-Safe

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Email/EmailBodyReceiptService.cs`
- Modify: `tests/InvoiceFlowAI.Application.Tests/Email/EmailBodyReceiptServiceTests.cs`

**Interfaces:**
- `EmailBodyReceiptService` is deterministic-only and must not depend on `IChatCompletionService`, access secrets, or make network calls.
- A recognized receipt returns through normal acceptance and candidate processing; an unresolved body returns a stable non-terminal receipt miss so attachment parsing can continue.
- Trace may include `InputKind`, source message UID, parser/route IDs, and stable status; it must not include email body, subject, or sender.

- [x] **Step 1: Add failing tests** asserting accepted deterministic receipt is produced without model access, unresolved receipt returns the attachment-fallback disposition, and sentinel subject/sender/body strings never appear in trace or errors.
- [x] **Step 2: Run the focused tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~EmailBodyReceiptServiceTests
```

Expected: current tests fail because the service depends on chat and stores subject/sender in trace.

- [x] **Step 3: Remove the chat dependency and implement deterministic-only parsing with a trace allowlist** while preserving `EMAIL_BODY_RECEIPT_CANONICAL`, candidate identity, stable receipt-miss mapping, and normal acceptance.
- [x] **Step 4: Run the focused tests** and the canonical receipt contract fixture check in the .NET test project.
- [ ] **Step 5: Wire the receipt-miss disposition into the extraction stage** so attachment parsing continues after an unresolved body. The stage is introduced in Task 10; verify this behavior there with a stage-level regression.

## Task 7: Implement PDF Rendering, Image Preparation, and SimdPaddleOCR

**Files:**
- Create: `src/InvoiceFlowAI.Application/Extraction/IOcrFallback.cs`
- Create: `src/InvoiceFlowAI.Application/Extraction/IPdfPageRenderer.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Ocr/PdfiumPageRenderer.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Ocr/SkiaImagePreprocessor.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Ocr/SimdPaddleOcrFallback.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Ocr/PdfiumPageRendererTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Ocr/SimdPaddleOcrFallbackTests.cs`

**Interfaces:**
- `IPdfPageRenderer.RenderAsync(DocumentSource, PdfRenderOptions, CancellationToken)` returns ordered in-memory `RenderedPage` values.
- `IOcrFallback.RecognizeAsync(DocumentIdentity, IReadOnlyList<RenderedPage>, CancellationToken)` returns page-numbered `OcrLine` values and aggregate confidence.
- Enforce maximum 2 pages, default DPI 200, width 4096, height 8192, byte limits, and configured OCR concurrency before allocating/requesting work.

- [x] **Step 1: Add failing tests** for page count/order, maximum dimensions, unsupported/corrupt files, image MIME and byte caps, cancellation, and local model initialization failure. Real OCR line geometry/confidence and native disposal remain residual verification items.
- [x] **Step 2: Run focused OCR tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~PdfiumPageRendererTests
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~SimdPaddleOcrFallbackTests
```

Expected: tests fail because production render/OCR adapters do not exist.

- [x] **Step 3: Implement the adapters** using the already-pinned package versions. Keep encoded images and native handles scoped to the operation; dispose on success, failure, timeout, and cancellation.
- [x] **Step 4: Run unit tests with fakes**. OCR concurrency is hard-capped at 2 and covered by a focused test. A bounded local asset smoke test and OCR-output geometry/confidence assertions remain deferred until Task 11 stages the pinned OCR model assets; no test may download a model from the network.

## Task 8: Implement DeepSeek Completion and Multi-Image Contract

**Files:**
- Modify: `src/InvoiceFlowAI.Application/Ai/IChatCompletionService.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Ai/DeepSeekChatCompletionService.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Ai/DeepSeekChatCompletionServiceTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Ai/DeepSeekRequestRedactionTests.cs`

**Interfaces:**
- Add `ChatImagePart(string ContentType, ReadOnlyMemory<byte> Bytes, int Width, int Height)` and change vision completion to accept `IReadOnlyList<ChatImagePart>` in page order.
- `CompleteTextAsync` and vision completion return `ChatCompletionResult` or throw `ChatCompletionException` with stable error codes; provider exception text is never exposed.
- Production adapter uses the migration design's DeepSeek endpoint/default model and retrieves the key from `ISecretStore` at request time.

- [x] **Step 1: Replace placeholder-oriented tests with fake-client tests** for text request, ordered multi-image content, token usage, model name, timeout retry, 429/Retry-After, 401/403, unsupported MIME, per-image/total request size, cancellation, and redaction.
- [x] **Step 2: Run the focused adapter tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~DeepSeekChatCompletionServiceTests
```

Expected: the valid text path fails with `NotImplementedException`; the multi-image contract does not compile until extended.

- [x] **Step 3: Implement Microsoft.Extensions.AI client construction and request mapping**. Retry only connection failures, timeouts, and explicit 429 within configured attempts; honor `Retry-After`; do not retry auth, size, image, schema, or caller cancellation errors.
- [ ] **Step 4: Add a run-scoped auth failure latch** at the Application/run boundary so one invalid credential does not trigger one 401 per candidate.
- [x] **Step 5: Run focused tests** and assert sentinel keys, prompts, image bytes, and raw provider exceptions do not appear in surfaced errors.

## Task 9: Implement Track A/B Field Extraction Router

**Files:**
- Create: `src/InvoiceFlowAI.Application/Extraction/IInvoiceFieldExtractor.cs`
- Create: `src/InvoiceFlowAI.Application/Extraction/InvoiceFieldExtractor.cs`
- Create: `src/InvoiceFlowAI.Application/Extraction/AiAuthenticationFailureGate.cs`
- Create: `src/InvoiceFlowAI.Application/Extraction/InvoiceExtractionPrompt.cs`
- Create: `src/InvoiceFlowAI.Application/Extraction/InvoiceResponseSchema.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Extraction/InvoiceFieldExtractorTests.cs`
- Create: `tests/InvoiceFlowAI.Application.Tests/Extraction/InvoiceResponseSchemaTests.cs`

**Interfaces:**
- `FieldExtractionRequest` carries candidate identity, original `DocumentSource`, optional embedded text, rules, and `AllowVisionFallback`; the router owns render/OCR/Track A/Track B ordering.
- `IInvoiceFieldExtractor.ExtractAsync` returns `FieldExtractionResult` containing normalized document or stable failure/manual-review result, route, and privacy-safe trace.
- `AiAuthenticationFailureGate` is scoped to one run and records the first confirmed 401/403 so subsequent candidates do not repeat the same credential failure.
- Track A sends text only to DeepSeek Flash at temperature `0.1`; Track B sends at most two ordered image parts after the documented fallback conditions.

- [ ] **Step 1: Add failing fake-chat tests** for Track A accepted success, OCR failure/too-short text, missing fields, invalid JSON/schema, acceptance failure, Track B success/failure, disabled fallback, and deterministic correction precedence.
- [ ] **Step 2: Run focused router tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~InvoiceFieldExtractorTests
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~InvoiceResponseSchemaTests
```

Expected: tests fail because the extraction router, prompt contract, and response schema do not exist.

- [ ] **Step 3: Implement versioned prompt/schema and route state machine**. Strip at most one Markdown fence, parse exactly one JSON object, reject ambiguous/nested-invalid responses, apply deterministic corrections, normalize, and run acceptance after each route. Use the run-scoped authentication gate to stop new requests after the first confirmed authentication failure.
- [ ] **Step 4: Run focused tests** and verify Track A success never calls vision, each documented Track A defect can invoke Track B when allowed, and Track B failure becomes one stable candidate result.

## Task 10: Connect Format/Special Dispatch to Extraction Stage

**Files:**
- Create: `src/InvoiceFlowAI.Infrastructure/Pipeline/DocumentExtractionStage.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Pipeline/DocumentExtractionStageTests.cs`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/Pipeline/ExtractionServiceRegistrationTests.cs`

**Interfaces:**
- `DocumentExtractionStage` implements the existing `IDocumentExtractionStage.ExecuteAsync(CandidateBatch, CancellationToken)` contract.
- It preserves `CandidateBatch.EffectiveTerminalResults`, processes each non-terminal item once, and returns one result per candidate while preserving identity, sequence, and processing revision.
- It dispatches format deterministic parsers, special parsers, and the generic OCR/AI router in the approved order; candidate-level failure never aborts other items.

- [ ] **Step 1: Add failing stage tests** for accepted XML/PDF fast paths, special-parser claimed failure, parser ambiguity, generic OCR/AI fallback, prior terminal result pass-through, mixed success/failure batches, cancellation, and one-result-per-candidate invariant.
- [ ] **Step 2: Run focused tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~DocumentExtractionStageTests
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~ExtractionServiceRegistrationTests
```

Expected: tests fail because no production `IDocumentExtractionStage` registration exists.

- [ ] **Step 3: Implement stage composition and scoped DI registrations** for format parsers, special parsers, OCR, renderer, chat service, normalizer, acceptance, and field extractor. Register `IDocumentExtractionStage` in the existing Infrastructure service collection extension.
- [ ] **Step 4: Run stage tests** and verify failures stay candidate-scoped while dependencies/configuration errors follow the migration design's node/run failure semantics.

## Task 11: Package and Verify OCR Model Assets

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.Infrastructure.csproj`
- Modify: `build/model-manifest.ps1`
- Modify: `build/windows/publish_dotnet_app.ps1`
- Modify: `src/InvoiceFlowAI.Application/Release/ReleaseManifestVerifier.cs`
- Modify: `tests/InvoiceFlowAI.Application.Tests/Release/ReleaseManifestVerifierTests.cs`

**Interfaces:**
- Publish output contains the pinned SimdPaddleOCR ChineseV6Tiny model/runtime assets and a hash-verified manifest entry.
- Pin and reference `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` version `1.4.2`, matching the migration design; package assets are restored from configured NuGet sources and copied from the build output, not downloaded by the installer or at runtime.
- Install/build consumes local pinned assets only; it does not download runtime/model files.
- Startup or OCR initialization rejects missing, size-mismatched, or hash-mismatched assets with the stable migration-design error.

- [ ] **Step 1: Add failing manifest tests** for required asset presence, SHA-256 mismatch, wrong architecture/size, and successful verification of a sanitized fixture.
- [ ] **Step 2: Run the focused release-manifest tests**:

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj --no-restore --filter FullyQualifiedName~InvoiceFlowAI.Application.Tests.Release.ReleaseManifestVerifierTests
```

Expected: any new model asset validation cases fail until the manifest is extended.

- [ ] **Step 3: Add the centrally pinned ChineseV6Tiny model package reference** and configure publish copy metadata so its model, metadata, and license files are staged under `publish/win-x64/models/` before manifest generation. Reject an empty or incomplete model directory.
- [ ] **Step 4: Update model manifest/runtime verification** to require the expected model asset set and verify relative path, byte length, and SHA-256. Do not add a runtime or installer network download step.
- [ ] **Step 5: Run manifest and verifier checks** against a temporary synthetic model directory, then run the Windows publish script:

```powershell
pwsh -File build/windows/publish_dotnet_app.ps1 -Configuration Release -PublishRoot publish/win-x64 -ProductVersion 1.0.0
```

Expected: the published `models/` directory is populated, both manifests contain its assets, and missing/corrupt model files fail closed.

## Task 12: Full Extraction Regression and Release Gate

**Files:**
- Modify/add extraction regression fixtures and tests from Tasks 2–11 only.
- No unrelated files.

**Interfaces:**
- The regression suite verifies all deterministic/special route cases, all Track A/B triggers, candidate isolation, privacy, and release asset integrity.
- Pairing and archive implementation remain outside this task; only extraction output compatibility with their existing candidate/domain contracts is checked.

- [ ] **Step 1: Run focused suites** for parsers, email receipt, OCR, DeepSeek, extraction router, and extraction stage.
- [ ] **Step 2: Run the full solution tests**:

```powershell
dotnet test InvoiceFlowAI.sln --no-restore
```

Expected: all tests pass. Record any existing unrelated warning separately; do not change unrelated behavior to make the suite green.

- [ ] **Step 3: Run Release build and Windows x64 publish validation** using the repository's existing publish script. Verify the OCR model manifest, WebView/release assets, and native dependency architecture checks pass.

```powershell
pwsh -File build/windows/publish_dotnet_app.ps1 -Configuration Release -PublishRoot publish/win-x64 -ProductVersion 1.0.0
```

- [ ] **Step 4: Review the final diff** for leaked content fields in trace/log/persistence, fixture licensing/privacy, unbounded byte/page/concurrency paths, and accidental changes outside the extraction slice.

## Execution Notes

- Run each focused test immediately after its implementation task before moving to the next task.
- Keep each task reviewable and independently testable; do not combine parser, OCR, and DeepSeek implementation into one large change.
- Do not run real DeepSeek requests in ordinary CI. A manually configured live smoke test may validate sanitized request/response shape, but must not save source documents, OCR, prompts, responses, or credentials.
- The repository currently ignores `docs/superpowers/`; this plan remains in the workspace and must not change ignore rules or be force-added without user approval.
- Do not commit unless the user explicitly asks for commits.