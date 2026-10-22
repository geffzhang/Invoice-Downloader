# Native Invoice Extraction Design

## Status

Approved design; implementation has not started. This document narrows the extraction scope from the migration design and treats `docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md` as authoritative. The Python implementation is a source for existing business rules and regression cases, not an authority when its behavior conflicts with the migration design.

## Goal

Implement the native .NET invoice extraction path using the migration design's parser architecture, local OCR, DeepSeek text and multimodal extraction, candidate-level failure semantics, and privacy requirements. Preserve existing deterministic business rules where they fit that architecture, and make the migration's intentional behavior changes explicit and testable.

The extraction system is not considered functionally equivalent until deterministic parsing, special cases, OCR, both AI routes, acceptance, and candidate-level outcomes pass the acceptance matrix in this design.

## Scope

This design covers:

1. Deterministic email-body, XML, OFD, and PDF-text extraction.
2. Special-case parser selection and migration of Python invoice-specific rules.
3. PDF rendering, image preprocessing, and local OCR.
4. DeepSeek Flash text and multimodal extraction, including routing, bounded retry, and stable failure mapping.
5. Field normalization, invoice acceptance, trace privacy, and focused regression tests.

It does not implement mailbox scanning, artifact URL recovery, pairing, archive orchestration, reports, or the UI. Those remain separate pipeline slices. All extraction outcomes must nevertheless use the existing candidate identity and result contracts so later stages can consume them without a parallel success path.

## Source Behavior and Migration Decisions

The Python `invoice_extractor.py` implementation is used to inventory deterministic rules and special cases. The .NET migration design takes precedence for architecture and route behavior.

The .NET route uses `Sdcb.SimdPaddleOCR` and DeepSeek Flash. It does not retain the Python GLM provider/model routing. The Python Track B transition, which is mostly exception-driven, is intentionally broadened according to the migration design: OCR text that is too short, missing required fields, invalid model JSON/schema, or local acceptance failure can trigger multimodal fallback.

Python deterministic behaviors to preserve through the appropriate parser or normalization layer include:

- Standard Chinese e-invoice v2 and legacy layouts.
- Railway ticket field reconciliation, including deterministic route/date fields overriding model drift.
- Hotel folio date interpretation, including `DD/MM/YY` cases.
- Foreign invoice normalization and CITS/GBT-specific extraction.
- Ride invoice and itinerary rules, including Didi-specific PDF behavior.
- Seller cleanup, amount selection and negative red-letter amounts, document type normalization, and category rules.
- Email-body receipts as a deterministic input route before attachment parsing.

These rules are migrated as focused parsers or deterministic normalization rules, not as copies of Python's monolithic route. Didi's local PDF parser moves before generic AI fallback as a special-case parser, rather than remaining a final fallback after Track B fails.

## Architecture and Route

The application layer owns route decisions and domain outcomes. Infrastructure owns PdfPig, PDFiumCore, SkiaSharp, SimdPaddleOCR, Microsoft.Extensions.AI, the DeepSeek endpoint, and secret retrieval. Provider SDK and HTTP types must not cross into Domain or Application contracts.

The candidate route is:

```text
Email body receipt
  -> deterministic XML/OFD/PDF text parser
  -> PDF text cleanup, segmentation, and deterministic field parser
  -> matching special-case parser
  -> generic OCR text route (Track A)
  -> multimodal route (Track B) when its trigger conditions are met
  -> InvoiceNormalizer
  -> InvoiceAcceptanceService
  -> one candidate-level terminal result
```

Format parsers run before special-case parsers. Special cases are independently registered and ordered by priority, with stable parser IDs, versions, source-kind and signature constraints, fixture IDs, and a registry fingerprint. A parser that does not match returns no result. Once a parser has positively claimed a document, a parse failure is retained as a stable parser outcome and must not silently fall through to generic AI. Same-priority competing matches produce `SPECIAL_PARSER_CONFLICT` and manual review. Priority is a deterministic dispatch order, not an implicit winner among multiple complete claims.

Email-body receipts are processed before attachments, using the existing candidate identity. They still pass through normalization, acceptance, duplicate handling, pairing, archive, audit, and reporting through the normal result path; they do not create a side-channel success.

For PDFs, PdfPig extracts text and `PdfInvoiceSegmenter` separates multiple invoices using document/page boundaries and invoice markers. Deterministic field parsing runs before rendering. Pages are rendered only when the text path is absent or insufficient. OFD XML is traversed before page fallback; a complete, accepted XML result prevents OCR and AI calls. OFD fallback may render at most two pages and must preserve unresolved XML conflicts rather than allowing a model to overwrite them.

The special-case registry includes at least railway ticket, accommodation folio, foreign invoice, and provider-specific layouts. Python CITS/GBT, ride itinerary, standard e-invoice, and Didi rules must be assigned to deterministic format or special-case parsers as appropriate and included in migration fixtures. The registry must not hard-code these cases inside the generic PDF parser.

## OCR and AI Adapters

The extraction route reuses the existing provider-neutral `IChatCompletionService` boundary. `IInvoiceFieldExtractor` (or its current equivalent) owns Track A/Track B routing and receives OCR text/lines, rendered pages, identity, rules, and the vision-fallback setting. The Infrastructure implementation owns DeepSeek request construction and maps provider failures to stable application error codes.

The current `IChatCompletionService.CompleteVisionAsync` accepts one image. It must be extended to accept an ordered collection of image parts so a single Track B request can carry up to two rendered pages. Requests must validate supported MIME types, dimensions, per-image byte limits, and total request size before network dispatch. Images remain in memory/request scope and are never written to logs, SQLite, audit events, or pipeline recipes.

Track A uses OCR text, ordered OCR lines, and document context only; it does not upload page images. Track B uses the same versioned extraction prompt and sends no more than two pages as inline image content. It never creates a public image URL. Both routes return structured JSON that must pass a strict schema parser; a single optional Markdown fence may be removed, but nested or ambiguous JSON is rejected. Model output is never directly treated as a domain object.

The versioned prompt preserves the Python field/business constraints carried into the migration design: invoice validity, date, purchaser, seller, amount, invoice code/number, type/category, and railway departure fields; seller cleanup; tax-inclusive amount preference and negative values; hotel folio date interpretation; and type/category allowlists. Custom rules are appended as a separate user-controlled section and cannot override system schema, safety, or acceptance constraints.

Image and render limits are enforced locally before requests. The first release uses the migration design's limits: up to two rendered pages, configured maximum dimensions, per-image and total request byte limits, bounded OCR/AI concurrency, and a bounded in-flight candidate count. OCR/model assets are versioned and included in the release manifest and configuration fingerprint. A missing or invalid required OCR asset is a startup/dependency or candidate-processing failure according to the migration design; it must not silently switch to an unbounded or weaker path.

## Routing and Acceptance

Track A runs local OCR when embedded PDF text is not useful, then sends OCR text to DeepSeek Flash. Track B is eligible when OCR output is too short, required fields are missing, the Track A response is invalid JSON/schema, or deterministic acceptance fails. `AllowVisionFallback=false` suppresses Track B and yields the corresponding stable review/failure result.

Both routes apply deterministic corrections before domain mapping. Examples include railway-ticket embedded-field reconciliation, provider-specific seller/type normalization, and date/amount normalization. Corrections must have explicit precedence and tests; a model cannot overwrite a deterministic field unless the corresponding rule explicitly permits it.

`InvoiceNormalizer` maps provider/parser payloads into domain types and preserves the input `DocumentIdentity`. `InvoiceAcceptanceService` validates required fields, amount/date/type consistency, target purchaser rules, and minimum confidence. Low confidence, unresolved conflicts, or inadequate fields become manual review; malformed document bytes and unsupported formats become stable candidate failures. Candidate-level failure is returned as data rather than thrown as a batch exception. Only infrastructure initialization failures, broken internal invariants, or other migration-design node/run-level failures may fault the pipeline node.

## Failure, Retry, and Trace Semantics

Stable failure codes follow the migration design. At minimum:

- `AI_TIMEOUT`, `AI_CONNECTION_FAILED`: retryable within the configured maximum attempts and backoff.
- `AI_RATE_LIMITED`, `AI_QUOTA_EXHAUSTED`: retry or terminate according to bounded retry policy and provider response metadata.
- `AI_AUTHENTICATION_FAILED`: non-retryable; activate one run-shared authentication failure gate and stop new requests using the same invalid credential.
- `AI_REQUEST_TOO_LARGE`, `AI_UNSUPPORTED_IMAGE`, `AI_INVALID_JSON`, `AI_SCHEMA_INVALID`: no blind retry. Track A schema/JSON failure may select Track B; Track B failure goes to manual review or the stable all-routes failure outcome.
- `EXTRACTOR_ALL_ROUTES_FAILED`: all permitted routes failed; candidate outcome is retained and the batch continues.
- Caller cancellation remains cancellation and is never reclassified as timeout or retried.
- A positively matched special parser failure does not silently enter generic AI. Same-priority parser ambiguity becomes manual review.

Only connection failure, timeout, and explicit 429 are retried by the DeepSeek adapter, using `Retry-After` where available or bounded exponential backoff. Authentication failure is surfaced once per run. Request size, image validation, and schema errors are rejected before or at the relevant boundary without repeating equivalent requests.

`ExtractionTrace` records route, Track A/B status, stable reason code, duration, model name, input kind, and an optional short response fingerprint. It must not include OCR lines/text, images, prompts, model response text, API keys, email subject/sender/body, or source absolute paths. Usage counters may be recorded only as non-sensitive numeric metadata.

## Existing Code Boundaries to Reconcile

- `IChatCompletionService` is already provider-neutral, but its vision method accepts only one image. Extend this boundary for ordered multi-image requests.
- `DeepSeekChatCompletionService` currently enforces an image byte limit but throws `NotImplementedException` for text completion; vision delegates to that placeholder. Production DeepSeek wiring and error mapping remain unimplemented.
- `ParserPipeline` currently runs every parser resolved by source kind and treats any two complete successes as a conflict. Replace or narrow this orchestration for the extraction route so positive match, priority, claimed-parser failure, same-priority conflict, and generic fallback match this design.
- `FixtureBackedParser` and current parser tests validate fixture transport, not production PDF/OFD/XML parsing or OCR. They cannot be used as evidence of implementation parity.
- `EmailBodyReceiptService` currently records subject and sender in trace and calls AI even after heuristic success. Align it with the migration design's deterministic receipt contract and trace privacy; do not make a model request necessary when deterministic extraction has already produced an accepted receipt.
- Existing NuGet references to PDFiumCore, PdfPig, SkiaSharp, SimdPaddleOCR, and Microsoft.Extensions.AI do not establish that production adapters or release assets are implemented.

## Delivery Phases

The work should be implemented as separate, testable phases:

1. **Deterministic extraction and dispatch**: format parsers, PDF text cleanup/segmentation, normalization/acceptance, special-parser registry semantics, and Python-derived deterministic rule fixtures. Include email-body receipt behavior and stable candidate outcomes.
2. **PDF and OCR infrastructure**: implement PdfPig extraction/segmentation, PDFiumCore rendering, SkiaSharp preprocessing, SimdPaddleOCR integration, bounded in-memory page handling, and packaged model verification.
3. **DeepSeek and two-track routing**: production text/vision adapter, multi-image request contract, versioned prompt/schema, stable error mapping, bounded retry/authentication gate, Track A/B route selection, and redaction tests.
4. **Parity and release acceptance**: complete the deterministic/special-parser matrix, route failure tests, candidate pipeline integration, release asset validation, and full solution regression run.

Pairing and archive behavior remain downstream contracts in this design. Extraction integration must emit the normal candidate result and invoice domain types so the separately planned pairing/archive work can consume them without extraction-specific bypasses.

## Acceptance Matrix

### Deterministic parsers

- Valid and invalid XML invoices, DTD/external entity rejection, missing invoice number, and stable malformed-input outcomes.
- OFD `original_invoice.xml`, Tag/CustomTag, and Content/ObjectRef paths; multi-page traversal; accepted XML skips OCR/AI; missing or incomplete XML uses at most two rendered pages; unresolved XML conflict is not overwritten by a model.
- PDF embedded text quality thresholds, multi-invoice segmentation, deterministic field parsing, empty/scanned PDFs, and page-boundary cases.
- Special parser positive match, no match, claimed-parser failure (no generic fallback), same-priority conflict, stable priority/order/fingerprint, and fixtures for railway, folio, foreign, provider, CITS/GBT, ride itinerary, and Didi behavior.
- Email-body receipt canonical marker, heuristic success without an unnecessary AI call, fallback behavior when deterministic parse is insufficient, preserved candidate identity, and no subject/sender/body leakage in trace.

### Rendering and OCR

- PDF/OFD rendering yields no more than two pages, respects DPI/dimension/byte caps, and rejects unsupported/corrupt inputs with stable outcomes.
- OCR output preserves page number, bounding boxes, stable line ordering, and confidence; empty/short OCR output routes to Track B only when allowed.
- Cancellation, timeout, image decode failure, OCR model load failure, and cleanup release native resources and temporary files.
- Release-manifest tests verify the pinned PaddleOCR model/runtime assets and hashes without downloading assets during install/build.

### DeepSeek and routing

- Track A sends text only and returns a normalized result without invoking Track B after accepted success.
- Track B triggers for each documented Track A condition and sends at most two ordered image parts with valid MIME, dimensions, and request-size limits.
- Track B disabled, Track B success, Track B invalid JSON/schema, rate limit, timeout, connection failure, authentication failure, unsupported image, oversize request, and cancellation all map to stable route/status/result combinations.
- Retry tests verify maximum attempts, `Retry-After`/backoff behavior, no retry for 401/403 or schema/size errors, and one run-shared authentication gate.
- Fake chat client tests cover prompt version, text and image content block shape, optional fence handling, strict single-object JSON/schema parsing, token/usage metadata, and response fingerprint behavior.
- Redaction tests place sentinel strings in OCR, image payloads, prompts, model responses, and provider exceptions, then assert none appear in logs, trace, audit, persistence, or RPC output.

### Candidate and integration semantics

- Every input candidate produces at most one final `CandidateProcessResult` preserving `DocumentId`, sequence, and processing revision.
- Candidate parser/OCR/model/acceptance failures do not fault the entire batch; node configuration and unrecoverable invariant failures follow run-level semantics.
- Retry preserves candidate identity and cancellation does not become a retryable failure.
- Parser/adapter tests use deterministic fakes and sanitized fixtures. At least one Windows x64 release validation exercises the actual OCR model assets and PDF render/OCR path; live DeepSeek is not required for ordinary CI and must not persist user data.

## Definition of Done

Extraction is complete only when all acceptance matrix sections pass, production adapters replace fixture-only or placeholder paths, release assets are verified, traces meet privacy constraints, and each candidate receives one stable terminal result. Passing parser fixture tests alone, compiling NuGet references, or successfully constructing a DeepSeek client does not constitute functional parity.

Pairing parity and archive-stage orchestration remain separate implementation slices. The extraction result contract must provide the normalized domain fields and stable candidate identity those later slices require, but this design does not claim their end-to-end integration is complete.