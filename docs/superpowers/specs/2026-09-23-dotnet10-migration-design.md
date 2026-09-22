# InvoiceFlowAI .NET 10 Migration Design

## 1. Goal and Scope

Rewrite InvoiceFlowAI as a Windows 11 desktop application targeting .NET 10. The new application will use WinUI 3 for the native shell and WebView2 for the existing HTML/JavaScript user interface. All runtime business logic will be implemented in C#; the released application will not depend on Python.

The migration target is full functional parity, including:

- IMAP mailbox scanning and invoice candidate filtering;
- PDF, OFD, and XML invoice handling;
- local OCR with `Sdcb.SimdPaddleOCR`;
- DeepSeek Flash text and multimodal extraction;
- invoice and supporting-document pairing;
- URL invoice recovery;
- archive naming and classification rules;
- Excel reports and manual-review output;
- run lifecycle, truth/audit evidence, diagnostics, retry, cancellation, and progress events;
- the existing HTML/JavaScript interaction model, adapted to a C# JSON/RPC backend.

The first release targets Windows 11 x64 only. Compatibility with the previous configuration and runtime-state formats is not required. Output behavior may be redesigned, but the functional results must remain equivalent.

## 2. Migration Strategy

Use vertical business slices with a stable frontend/backend contract. The Python implementation remains a behavioral reference and source of test samples during migration, but is not part of the new runtime.

The slices are ordered as follows:

1. WinUI 3/WebView2 shell and JSON/RPC bridge.
2. Domain contracts, run lifecycle, cancellation, progress, and diagnostics.
3. Local file pipeline for XML, OFD, PDF, OCR, field extraction, validation, and archive output.
4. IMAP scanning and candidate filtering.
5. DeepSeek Flash text and multimodal extraction through `IChatClient`.
6. URL recovery and provider-specific handling.
7. Pairing, advanced rules, reports, truth audits, and release hardening.

Each slice must have an executable test surface before the next slice expands the system.

## 3. Solution Architecture

```text
src/
  InvoiceFlowAI.App/
    App.xaml
    MainWindow.xaml
    WebView/
    Rpc/
  InvoiceFlowAI.Application/
    Runs/
    Commands/
    Events/
    Services/
  InvoiceFlowAI.Domain/
    Invoices/
    Candidates/
    Pairing/
    Rules/
    Truth/
  InvoiceFlowAI.Infrastructure/
    Mail/
    Ocr/
    Ai/
    Documents/
    Browser/
    Archive/
    Reports/
    Persistence/
    Security/
  InvoiceFlowAI.Contracts/
    Rpc/
    Models/
    Errors/

tests/
  InvoiceFlowAI.Domain.Tests/
  InvoiceFlowAI.Application.Tests/
  InvoiceFlowAI.Infrastructure.Tests/
  InvoiceFlowAI.App.Tests/
```

### Responsibilities

- `InvoiceFlowAI.App`: WinUI window, WebView2 initialization, lifecycle, DPI, packaging, and bridge wiring. It must not contain invoice business rules.
- `InvoiceFlowAI.Contracts`: JSON-serializable commands, events, DTOs, stable reason codes, and frontend-facing schemas.
- `InvoiceFlowAI.Application`: run orchestration, admission validation, cancellation, retry policy, stage transitions, concurrency limits, and progress emission.
- `InvoiceFlowAI.Domain`: invoice entities, parse results, classification rules, pairing rules, validation, and truth contracts. This layer has no WebView2, HTTP, database, or vendor SDK dependencies.
- `InvoiceFlowAI.Infrastructure`: concrete IMAP, document, OCR, AI, browser, archive, report, persistence, and credential implementations.

The structure follows the useful `Services/Parsers/Exporters` separation in `E:/GitHub/qingpiao/src/QingPiao`, while replacing direct concrete dependencies with interfaces suitable for desktop orchestration and tests.

## 4. WebView2 Contract

The frontend communicates with the backend through JSON/RPC messages. Every request has a request ID; long-running operations also have a run ID.

Example command:

```json
{
  "id": "request-1",
  "method": "run.start",
  "params": {
    "dateFrom": "2026-09-01",
    "dateTo": "2026-09-23",
    "savePath": "C:/Invoices"
  }
}
```

Progress and terminal events are structured and versioned:

- `run.stageChanged`
- `run.progress`
- `run.documentResult`
- `run.failed`
- `run.completed`
- `run.cancelled`

The bridge owns transport, request correlation, serialization, and event forwarding. It does not know how invoices are scanned or parsed.

## 5. Run Lifecycle

`RunCoordinator` owns the lifecycle:

```text
Created
  -> Validating
  -> ScanningMailbox
  -> CollectingCandidates
  -> RecoveringUrls
  -> ExtractingDocuments
  -> PairingArtifacts
  -> Archiving
  -> ExportingReport
  -> Completed
```

A run may also end as `Cancelled`, `Failed`, `PartialSuccess`, or `NeedsManualReview`. A single malformed document must not terminate a batch. Only run-level failures such as invalid credentials, unavailable storage, or unrecoverable persistence errors terminate the run.

Every stage receives a `CancellationToken`, writes audit events, and emits a safe progress event. Credentials, raw authorization values, and unredacted sensitive URLs are never included in events or logs.

## 6. Document and OFD Processing

The document layer exposes independent interfaces:

```csharp
public interface IInvoiceParser
{
    Task<IReadOnlyList<InvoiceParseResult>> ParseAsync(
        DocumentSource source,
        CancellationToken cancellationToken);
}
```

PDF processing uses text extraction where available and page rendering for image-only pages. XML processing uses a secure XML reader and maps supported invoice schemas into domain objects.

### OFD parser requirement

OFD must retain a dedicated invoice-structure parser. It must not rely only on generic text extraction.

The implementation will follow the behavior of `E:/GitHub/qingpiao/src/QingPiao/Parsers/OfdInvoiceParser.cs` without taking that project as a runtime dependency:

1. Open the OFD package as a bounded ZIP archive.
2. Prefer `Doc_0/Attachs/original_invoice.xml` for paper/electronic invoice structures.
3. Otherwise inspect `Doc_0/Tags/Tag.xml` and `Doc_0/Tags/CustomTag.xml`.
4. Load `Doc_0/Pages/Page_0/Content.xml` and construct `Object ID -> TextObject` mappings for `ObjectRef` values.
5. Extract invoice number, issue date, buyer, seller, amounts, tax, and item details.
6. Return a manual-review result when invoice data is absent or incomplete instead of fabricating fields.

The parser must disable DTD and external entities, reject ZIP path traversal, limit entry count and decompressed size, and tolerate the namespace variations covered by the reference implementation. Tests must include all three XML paths, missing data, malformed XML, malformed ZIPs, oversized archives, and empty invoice numbers.

## 7. OCR and AI Extraction

The OCR and AI pipeline is:

```text
PDF/OFD page image
  -> SimdPaddleOCR local OCR
  -> OCR text, boxes, and confidence
  -> DeepSeek Flash structured extraction
  -> local field normalization and validation
  -> invoice domain object
  -> DeepSeek Flash multimodal review when needed
```

### Local OCR

Use:

- `Sdcb.SimdPaddleOCR`;
- the appropriate Chinese model package, initially `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny`;
- ImageSharp or SkiaSharp for decoding and pixel conversion.

`PaddleOcrAll` is loaded once and reused for the application lifetime. OCR runs on bounded background workers. `LineWorkerCount`, page concurrency, and image dimensions are limited to prevent memory spikes. The first release uses ordinary self-contained `win-x64` JIT/ReadyToRun publishing rather than Native AOT.

```csharp
public interface IInvoiceOcr
{
    Task<OcrDocument> RecognizeAsync(
        DocumentImage image,
        CancellationToken cancellationToken);
}
```

### DeepSeek Flash through Microsoft.Extensions.AI

DeepSeek Flash is the only remote AI provider in the new design. It is accessed through the OpenAI-compatible endpoint using `Microsoft.Extensions.AI` and `Microsoft.Extensions.AI.OpenAI`.

```csharp
IChatClient chatClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions
    {
        Endpoint = new Uri("https://api.deepseek.com")
    })
    .GetChatClient(modelName)
    .AsIChatClient();
```

The application layer depends on an internal interface, while the infrastructure layer owns `IChatClient`:

```csharp
public interface IInvoiceFieldExtractor
{
    Task<InvoiceExtractionResult> ExtractAsync(
        OcrDocument ocr,
        CancellationToken cancellationToken);
}
```

Text requests contain OCR text and coordinates. Multimodal requests contain a strict extraction prompt plus image `DataContent`. Both paths use the same typed response validation and local business validation. The model name, endpoint, temperature, token limit, timeout, and retry policy are configuration values; the exact DeepSeek Flash model identifier must be verified against the deployed DeepSeek account before implementation is finalized.

The system first tries OCR text extraction. If OCR confidence is low, required fields are missing, or local validation fails, it sends the source image to DeepSeek Flash for multimodal review. GLM configuration, packages, error codes, and runtime paths are excluded from the new solution.

Remote AI failures are converted into stable errors for timeout, authentication, rate limit, quota exhaustion, invalid response, and unsupported multimodal input. Unit tests use a fake `IChatClient` and never require a live API key.

## 8. Storage and Security

Use SQLite for run indexes, invoice history, audit events, and manual-review state. Store application data under `%LocalAppData%/InvoiceFlowAI`; store user-selected invoices and reports in the selected output directory.

Store mailbox authorization codes and the DeepSeek API key with Windows DPAPI or Credential Manager. Do not write credentials to JSON settings, logs, audit records, crash reports, or WebView events.

URL evidence stores only sanitized domains, stable hashes, and stage metadata. Original mail and invoice images remain local unless the user-configured AI policy explicitly sends an image to DeepSeek.

## 9. External Integrations

- IMAP: MailKit-based adapter with provider settings and bounded mailbox scanning.
- URL recovery: Playwright for .NET and provider-specific download adapters.
- Reports: ClosedXML for invoice summary, item detail, and manual-review workbooks.
- PDF: text extraction plus page rendering for OCR-required documents.
- OFD: dedicated ZIP/XML invoice parser described above.
- AI: `IChatClient` backed by `Microsoft.Extensions.AI.OpenAI` and DeepSeek's OpenAI-compatible endpoint.

## 10. Error Model

Frontend errors use stable reason codes rather than raw exception text:

```json
{
  "runId": "run-1",
  "stage": "document_extraction",
  "reasonCode": "OFD_INVOICE_XML_NOT_FOUND",
  "userMessage": "OFD 文件中未找到可识别的发票数据。",
  "retryable": false,
  "documentId": "document-1",
  "detailsAvailable": true
}
```

Categories include input validation, single-document failure, retryable network/AI failure, authentication, persistence, disk, WebView2, and system failures. User-facing messages are safe and localized; diagnostics retain redacted technical details.

## 11. Testing and Acceptance

### Unit tests

Cover domain validation, amount and tax calculations, rules, pairing, deduplication, archive naming, path security, OFD XML variants, OCR normalization, DeepSeek response validation, and report mapping.

### Integration tests

Verify WebView2 RPC dispatch, the complete local file pipeline, real OFD samples, fake `IChatClient` responses, retry/cancellation, SQLite persistence, audit events, and Excel generation.

### Windows end-to-end tests

On a clean Windows 11 x64 environment verify first launch, WebView2 loading, cancellation, network interruption and retry, large batches, recovery after WebView2 failure, self-contained startup, bundled OCR model loading, and no Python dependency.

Acceptance is behavioral, not code-translational:

- equivalent candidate sets for the same mailbox samples;
- equivalent invoice fields and manual-review classifications for PDF/OFD/XML samples;
- correct pairing, archive, deduplication, report, and audit outcomes;
- stable and explainable failures;
- a clean Windows 11 machine runs the release package with only the required DeepSeek configuration.

## 12. Risks and Open Verification Items

- Verify DeepSeek Flash's exact model identifier and multimodal content format through the target endpoint.
- Benchmark SimdPaddleOCR on small-font, rotated, low-resolution, and scanned invoice pages.
- Verify PDF/OFD rendering quality before OCR.
- Validate OFD samples from both `original_invoice.xml` and `Tag.xml`/`CustomTag.xml` variants.
- Confirm Playwright provider flows and packaged browser behavior.
- Measure memory under bounded OCR concurrency.
- Confirm Chinese Excel column widths and self-contained package size.

These are verification tasks for implementation, not reasons to change the approved architecture.
