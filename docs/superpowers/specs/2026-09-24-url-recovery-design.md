# Native URL Recovery Design

## Status

Approved; implementation is partially complete as of 2026-09-24. Grouping/contracts, provider strategies, worker isolation, selected-artifact pipeline wiring, and source-group history linking are implemented and covered by tests. Browser egress proxy and Windows process sandbox are not implemented, so browser fallback remains disabled. The worker-to-stage composition test does not cover the full mailbox-to-archive path. The full solution currently passes 420 tests; no changes have been committed.

## Goal

Bring the native C# URL recovery path close to the existing Python mail/link recovery behavior while preserving the current pipeline's one-primary-result contract, URL privacy, SSRF controls, and bounded resource use.

## Scope

The design covers provider URL grouping and recovery, artifact capture and selection, browser fallback, worker isolation, and pipeline integration. Implementation is divided into independently testable subprojects:

1. Grouping, expected-field evidence, artifact selection contract, and history identity.
2. Direct-provider and Baiwang recovery strategies.
3. Generic browser fallback with a complete outbound-network policy boundary.
4. Worker isolation and end-to-end pipeline integration.

Each subproject receives its own implementation plan after this design is approved. They are not one implementation batch.

## Current State

- MailKit emits URL candidates from plain text and HTML links. Candidate bodies are not copied into URL candidate metadata.
- `MailboxUrlCandidateDiscovery` classifies seven direct-invoice families and basic Baiwang signals, and extracts a limited expected-field allowlist.
- `CandidateCollectionStage` currently emits one work item per URL. `ProviderGroupId` is a message UID and provider-family string, not an active grouping operation.
- `PublicUrlRecoveryClient` uses `PublicUrlPolicy` and pinned HTTP transport for bounded GET requests. It implements the Nuonuo scan detail POST flow and validates returned artifact signatures. Other direct providers currently use generic GET and PDF validation.
- `UrlRecoveryStage` returns recovered bytes as a single candidate work item, updates content type, length, and extension, and converts safe recovery errors into candidate terminal results.
- The Python direct recovery probes multiple links and records multiple artifacts before selecting one primary result. The direct selector normally chooses one PDF using expected-field matches and conflict checks. The current Python selector also has XML-only fallback branches; this legacy edge behavior is part of compatibility unless a separately approved behavior change removes it.
- The Python Baiwang path probes wrapper links and multiple artifact kinds, uses XML/OFD metadata to validate or enrich PDF candidates, and returns a selected primary result rather than expanding all captured artifacts into the downstream main chain.
- There is no complete C# URL browser fallback, isolated recovery worker, or IMAP-to-final-archive URL recovery integration test yet.

## Behavioral Contract

### Grouping

Candidates are grouped transiently by `(AccountId, Mailbox, UidValidity, MessageUid, ProviderFamily)`. Unknown-provider URLs remain independent. This prevents separate messages, mailboxes, accounts, or UID generations from being merged. The existing `ProviderGroupId` may remain a correlation hint, but it is not sufficient as the full group key.

URLs within a group preserve discovery order and are deduplicated by their exact normalized absolute URI for the duration of the run. The raw URLs, query parameters, message body, and capability values are not copied to persistent metadata, logs, or user-facing failures.

### Expected fields

Expected-field values carry an internal evidence source and priority during merge:

1. Provider URL query values take precedence over subject-derived values.
2. Subject-derived values take precedence over body-derived values.
3. For equal-priority non-empty values, the first value in discovery order wins.

Persisted candidate metadata contains only the approved normalized invoice fields. Raw messages and raw URL query values remain transient.

### Artifact probing and primary selection

Each provider strategy may capture multiple bounded artifacts across the group's URLs. Supported recognized kinds are PDF, XML, and OFD. A capture is accepted only after kind detection and format-signature validation; MIME and filename are hints, not sufficient proof by themselves.

The strategy records a transient artifact inventory containing kind, byte length, SHA-256 digest, source URL ordinal, sanitized resolved origin, and selection/match reason. It does not persist raw source or resolved URLs. Temporary bytes are confined to the recovery job and removed on completion or failure, except for the selected primary result passed to the pipeline.

Selection produces at most one primary artifact for the existing main-chain pipeline. The direct selector prefers a PDF that matches expected invoice identity, rejects hard entity mismatches, and accepts a single PDF only when there is no conflicting evidence. XML and OFD may supply invoice metadata to validate or enrich a PDF but are not independent main-chain outputs. To match the current Python selector exactly, retain its explicit XML-only fallback for a matched XML or a single non-conflicting XML when no PDF exists. Multiple unmatched PDFs and conflicting entity evidence produce safe failure reasons rather than arbitrary selection.

Baiwang selection follows its provider rules: prefer a matching non-wrapper PDF; use matching XML/OFD metadata to validate or enrich the selected PDF; permit an unambiguous single PDF fallback only under the Python rules; otherwise return a safe mismatch or no-confirmed-PDF failure.

The `UrlRecoveryStage` continues to emit one `CandidateWorkItem` per grouped recovery result. It updates filename extension and content type from the selected artifact, not from email preference alone. Captured non-selected artifacts are diagnostic evidence and do not become independent downstream candidates.

### Provider strategies

`PublicUrlPolicy` and the pinned transport remain the shared networking boundary. Provider-specific request construction, response parsing, retries, deadlines, artifact discovery, and selection move behind a strategy interface selected by `ProviderFamily`. Strategies return a common captured-artifact inventory and one selected primary result or a typed safe failure.

The implementation sequence is Nuonuo compatibility first, then the remaining direct families based on Python behavior and fixtures, then Baiwang wrapper and multi-artifact behavior. A provider must not be marked supported until its request sequence, redirects, selected output, and failure mapping have focused fixture coverage.

### Generic browser fallback

Browser fallback is a separate strategy used only when no direct provider strategy applies. The browser must not have unrestricted direct network access. All outbound requests, including navigation, redirects, frames, subresources, popups, downloads, and WebSockets, must pass through an enforcing egress boundary that validates DNS results, rejects non-public destinations, pins connections to validated addresses, and enforces response limits.

Playwright request interception alone is not considered sufficient for SSRF protection because browser DNS resolution and socket connection can occur outside the application transport. The selected design is a local policy-enforcing proxy with direct browser egress blocked by the worker/container or host firewall. If that boundary cannot be enforced on the target platform, browser fallback remains disabled rather than running with weaker controls.

Navigation time, total job time, redirect count, downloaded bytes, number of captured artifacts, and browser process lifetime are bounded. Browser processes and temporary outputs are cleaned up on success, failure, timeout, and cancellation.

### Worker isolation

Provider and browser recovery run in a bounded child worker process. The worker contract is versioned and uses a per-job directory containing an atomic request manifest, bounded result manifest, and temporary artifact files. Requests may contain raw capabilities only inside that access-restricted temporary directory; they are never logged or retained after cleanup.

The parent validates protocol version, result count, path confinement, file existence, byte length, digest, and artifact signature before accepting outputs. Output paths must resolve inside the job directory; path traversal, reparse-point escapes, malformed manifests, and unsupported result versions fail closed. Timeout or cancellation terminates the worker process tree and removes the job directory.

### Identity and history

The existing HMAC-based URL identity remains the source-group identity used to skip previously completed recovery. Selected artifacts need a distinct stable document identity derived through an identity-factory API from the source-group identity, artifact kind, and content digest; raw URLs are not identity inputs outside the existing HMAC boundary.

On successful group recovery, persistence records the relationship between the source-group identity and selected artifact identity so history lookup on a future run recognizes completed URL recovery. Non-selected diagnostic artifacts do not create archive candidates or independent history rows. Duplicate selected content is deduplicated using kind and content digest within the run.

## Failure Semantics

- Unsafe URLs, unsafe redirects, and peer-address mismatches map to a non-retryable safe policy failure.
- Network/server failures may be retryable according to provider strategy policy; raw exception text is not exposed.
- Deadline exhaustion maps to timeout and remains distinguishable from caller cancellation; caller cancellation propagates without creating a terminal failure.
- Invalid or oversized artifacts are not selected. If no artifact meets the provider selector contract, return a stable provider-specific reason code without URLs, query values, response bodies, or credentials.
- XML-only behavior follows the existing Python direct selector as described above; it is not interpreted as permission to fan out XML into the main chain.

## Security and Resource Invariants

- Every URL and redirect is revalidated; each connection is pinned to the validated public address and its peer is verified.
- No request body or capability field crosses an origin boundary. Redirect method changes follow explicit provider-safe rules.
- Raw URLs, query strings, message bodies, raw response bodies, and raw exception text never enter persistent candidate metadata, logs, traces, or user-facing failures.
- DNS answers containing any non-public address fail closed.
- HTTP response size, decompressed bytes, URL count per group, artifact count per group, concurrency, retries, and total elapsed time have explicit finite limits.
- Temporary files and worker processes are cleaned up on all terminal paths.

## Verification Strategy

- Unit tests cover grouping scope, exact URI deduplication, expected-field provenance/precedence, artifact signature validation, selector decisions, identity derivation, and source-group history linking.
- Provider fixture tests compare Python and C# request sequences, captured artifact kinds/counts, selected primary artifact, and safe failure reason codes. Fixtures contain synthetic URLs, tokens, and invoice values only.
- Transport tests cover DNS rebinding defenses, public-IP pinning, redirect revalidation, cross-origin body suppression, decompressed-size limits, timeout, and cancellation.
- Browser tests run against local controlled HTTP fixtures through the enforcing proxy and prove private/loopback destinations are blocked before reaching the fixture server.
- Worker tests cover valid protocol exchange, malformed/unsupported manifests, output path escapes, hash/signature mismatch, oversize output, timeout process-tree termination, cancellation, and directory cleanup.
- Pipeline integration tests cover IMAP URL discovery through candidate grouping and recovery fan-out into a fake extraction/pairing/archive boundary. A composition smoke test verifies service registration. Full archive verification is added when the extraction and archive stages are implemented in C#.
- The full solution test suite remains the final regression gate; the existing Installer `Debug|Any CPU` solution warning is unrelated and is not part of this design.

## Non-Goals

- Add the Bitzsoft Nuonuo Open API package for mail short-link recovery; its documented APIs do not provide this workflow.
- Archive all captured XML/OFD artifacts as independent invoices.
- Weaken SSRF protections to make browser fallback easier to ship.
- Port unrelated invoice extraction, pairing, or archive behavior except where needed to establish URL recovery integration contracts.

## Decisions Requiring Review

1. Preserve Python's current XML-only direct selector fallback despite the common path selecting one PDF. Removing that fallback is a behavior change and requires an explicit parity decision.
2. Browser fallback remains unavailable on platforms where direct browser egress cannot be blocked and policy-proxied.
3. Provider strategies ship incrementally and are considered supported only after fixture-based parity coverage exists.
