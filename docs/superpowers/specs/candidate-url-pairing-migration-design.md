# Candidate, URL Recovery, and Pairing Migration Design

## Goal

Complete the missing C# business behavior for candidate collection, URL invoice recovery, and artifact pairing while preserving the observable behavior of the Python pipeline.

## Confirmed Decisions

- URL links are discovered from both plain-text and HTML email bodies. Raw bodies are transient scanner inputs only; they are not included in scan results, candidate metadata, logs, or persistence.
- Provider-derived email fields are limited to structured values needed by recovery, initially `invoice_number`, `seller`, `invoice_date`, and `preferred_kind`. They remain run-scoped and are not logged or persisted as candidate metadata.
- Candidate identity includes account/mailbox context, UIDVALIDITY, message UID, resource kind, and a resource fingerprint. MIME attachment fingerprints use SHA-256 of payload bytes. URL fingerprints use HMAC-SHA-256 over the complete canonical URL so query-based resource distinctions are preserved without persisting URL or token text. The opaque persisted identity includes a fingerprint/key version; key material comes from the application's stable secret store.
- URL recovery is implemented natively in C#. Python remains a behavioral oracle, not a production runtime dependency.
- URL behavior includes the generic Playwright page-to-PDF path, Baiwang recovery, the seven direct invoice families, provider-group serialization, cross-group concurrency, bounded worker deadlines, staged output validation/promotion, cancellation, and progress reporting.
- URL security validates public-address policy, every redirect, resolved/connected peers, allowed ports, proxy behavior, and browser subrequests. Unsafe or unverified destinations fail closed.
- Pairing is scoped to successfully extracted documents from the same mailbox in the current run. Existing `PairingEngine` remains the matching algorithm.
- Pairable roles are Ride invoice/itinerary and Hotel invoice/folio. An unmatched or ambiguous pairable candidate is changed to `ManualReview`; matched candidates retain their extraction status. Non-pairable document types are not promoted to manual review by pairing.

## Data Flow

1. `MailKitMailboxSession` exposes transient plain-text and HTML body parts to the scanner.
2. `MailKitMailboxScanner` projects attachment candidates and extracts ordered URL candidates. A URL candidate carries source mailbox, account/mailbox UID context, a `Uri`, inferred provider family, an opaque provider-group identity, and the permitted structured expected fields. It never carries the raw body.
3. `MailboxScanResult` transports attachments and URL candidates into `ICandidateCollectionStage`.
4. Candidate collection preserves deterministic source ordering, assigns stable `DocumentIdentity` values, applies attachment `main_chain`/`retain_only`/`drop` decisions, and deduplicates by identity within the run and against existing processing history.
5. URL recovery groups candidates by provider group, serializes work inside each group, runs independent groups concurrently within policy limits, and returns validated artifact bytes or a safe terminal failure. URL-derived filenames and metadata are sanitized before entering extraction.
6. `IDocumentExtractionStage` parses both attachment and recovered URL artifacts through the existing parser pipeline.
7. Pairing orchestration projects eligible `CandidateProcessResult` values into `PairingDocument` values, calls `PairingEngine`, then maps unmatched/ambiguous IDs back to `ManualReview` results using stable safe reason codes.
8. Archive and report stages receive the resulting `PairingBatch`; the pairing engine's deterministic assignments and ambiguity output remain intact.

## Candidate and URL Contracts

The scan contract will add a URL-candidate collection without changing the meaning of existing attachment candidates. A URL candidate contains:

- Source account/mailbox identity, UIDVALIDITY, and message UID.
- The source URL as transient run data only.
- An inferred provider family and opaque group identity.
- A fixed-key structured expected-fields map containing only recovery fields.
- A deterministic sequence position relative to other candidates from the same message.

Identity rules:

- The source component is account ID + mailbox + UIDVALIDITY + message UID; UID alone is not globally unique and may be reused after UIDVALIDITY changes.
- The resource component is `attachment:<sha256(payload)>` or `url:<key-version>:<hmac-sha256(canonical-url)>`.
- Canonicalization must not discard query parameters that distinguish provider resources. The HMAC output, never the canonical URL, is the cross-run URL fingerprint.
- HMAC key rotation requires a new key version and an explicit compatibility policy; silently changing the key would change candidate identities.

No candidate trace, failure, metric label, or log may contain raw URL/query values, email body text, credentials, or unsanitized exception messages.

## URL Recovery Behavior

Provider families are `chinatax_direct_invoice`, `bwjf_signed_invoice`, `fpyun_direct_invoice`, `nuonuo_scan_invoice`, `pdd_direct_invoice`, `jdcloud_direct_invoice`, and `kpbyd_direct_invoice`, plus the Baiwang provider and generic browser fallback.

The native C# implementation must match the Python provider selection, expected-field extraction, URL grouping/order, direct download-kind inference, fallback order, and artifact acceptance rules. Browser and network work runs behind a bounded worker boundary. A timeout terminates the worker and its owned browser/network child processes; only fully validated staged files may be promoted. Cancellation stops queued work, interrupts active work, and cleans all unpromoted artifacts.

All URL fetches, redirects, browser navigations, and browser subrequests must pass the same outbound policy. DNS resolution is checked before connection, redirects are revalidated, and the connected peer is verified against the validated address set to reduce DNS-rebinding exposure. Providers may further restrict host/port combinations; policy rejection is terminal and does not fall through to a less restrictive transport.

## Pairing Projection and Status Mapping

- `RideInvoice` maps to `PairingRole.RideInvoice`; `RideItinerary` maps to `PairingRole.RideItinerary`.
- `HotelInvoice` maps to `PairingRole.HotelInvoice`; `HotelFolio` maps to `PairingRole.HotelFolio`.
- Pairing amount uses `InvoiceDocument.TotalAmount` when present, otherwise `InvoiceDocument.Amount`; business date uses `InvoiceDate`.
- Merchant tokens are normalized from the seller/merchant fields. Provider comes from trusted extraction metadata, not from an arbitrary filename or URL.
- `PairingDocument.Id` maps back to the candidate's stable `DocumentId`; `SourceMessageUid` and artifact path are preserved for scoring and archive output.
- A uniquely matched pair remains `Resolved`. A pairable unmatched result becomes `ManualReview` with a stable unmatched reason code. Candidates in an ambiguity component become `ManualReview` with a stable ambiguity reason code. The exact review message is safe and contains no source URL or OCR/body text.
- Non-pairable types and candidates that did not resolve successfully are passed through without pairing status changes.

## Work Breakdown

The migration is split into independently reviewable sub-projects:

1. **Contracts and Python golden fixtures.** Add the scan URL-candidate contract and a synthetic, versioned golden corpus for extraction/provider/grouping and pairing projection expectations. Do not wire URL execution into the live pipeline in this slice.
2. **Candidate collection and pairing orchestration.** Add run-local ordering, stable identity creation, deduplication/history mapping, candidate processing, pairing projection, and status mapping. Reuse the current pipeline graph and `PairingEngine`.
3. **Native URL recovery.** Implement outbound URL security, bounded worker/process lifecycle, provider grouping/scheduling, direct providers, Baiwang, and Playwright fallback with staged output validation.
4. **End-to-end activation.** Connect scanner link extraction to collection and recovery, register concrete stage implementations, then verify the complete run from scanned mail through pairing and archive inputs.

Each implementation sub-project gets its own detailed plan before coding. All behavior changes use red-green-refactor; each sub-project ends with focused tests before the full solution test suite.

## First-Slice Golden Corpus

The first fixture corpus is synthetic and versioned. It covers:

- Plain-text URL extraction and HTML anchor extraction.
- Duplicate links across body and HTML, with deterministic first-seen ordering.
- Provider-family inference for all seven direct invoice families and Baiwang.
- Expected-field extraction for invoice number, seller, date, and preferred artifact kind.
- Provider-group identity stability without exposing body text, raw URL, or query tokens in serialized expected output.
- Pairing projection for all four eligible roles, plus non-pairable document types.
- Pairing status outcomes for a unique match, unmatched pairable documents, and an ambiguous component.

Fixtures must contain fabricated domains/identifiers and must never be populated from real mailbox data.

## Validation

- Contract tests load the fixture corpus and verify schema version, required fields, and allowed field names.
- Golden parity tests compare the C# behavior introduced by each later sub-project with checked-in expected output derived from the Python reference functions.
- Security tests assert that prohibited destinations fail closed, redirect targets are revalidated, and safe failures never expose URL/body/secret text.
- Worker tests cover deadline enforcement, process-tree cleanup, cancellation, output staging/promotion, content hash verification, and cleanup on failure.
- Provider tests cover provider recognition, request/response shape, expected-field use, fallback ordering, artifact validation, and provider-group serialization.
- Pairing tests cover same-mailbox run scope, all role projections, matched/unmatched/ambiguous status mapping, and pass-through for non-pairable or non-resolved results.
- The final gate is the full solution test suite on the repository's pinned .NET SDK, without changing `global.json`.
