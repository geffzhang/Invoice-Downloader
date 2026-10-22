# Native URL Recovery Implementation Plan

## Goal

Bring the C# URL candidate and recovery path to behavioral parity with the Python mail/link recovery flow while retaining URL privacy, SSRF defenses, bounded resource use, and the existing pipeline packet contracts.

## Completed in the current implementation

- [x] Preserve HTML body in the transient MailKit message projection; emit URL candidates only, without retaining the body in `MailboxScanResult`.
- [x] Discover absolute HTTP(S) links from plain text and HTML href attributes, decode HTML entities, and deduplicate links in discovery order.
- [x] Classify the seven direct-invoice provider URL shapes plus basic Baiwang hints.
- [x] Extract a limited allowlist of invoice metadata from direct-provider and Baiwang message text.
- [x] Reject unsupported schemes, URL credentials, localhost names, non-approved ports, non-public literal/DNS addresses, and mismatched connected peers.
- [x] Follow redirects manually, revalidate every destination, pin each socket to the validated DNS answer, enforce a total request deadline, and bound decompressed response bytes.
- [x] Connect the bounded fetch client to the application `IUrlRecoveryStage`; map safe fetch failures to candidate terminal results and preserve prior terminal results.
- [x] Implement the Nuonuo scan-link flow: resolve the short link, POST the provider detail form, safely download returned XML/PDF/OFD artifacts, prefer the expected kind, and validate file signatures.
- [x] Block cross-origin redirects from carrying Nuonuo capability form fields to another host.

## Remaining work

### Provider behavior parity

- [ ] Port the remaining direct-provider request/response strategies (six families), including required headers, query normalization, POST bodies, token/signature handling, retry rules, output selection, and provider-specific deadlines.
- [ ] Port Baiwang link grouping and multi-artifact discovery, while keeping secrets and raw URLs out of persisted metadata and logs.
- [ ] Port provider-group URL coalescing and expected-field merge precedence.
- [ ] Add fixture-driven tests against the Python golden cases for provider output count, file type, metadata, and failure-code mapping.

### Generic browser fallback

- [ ] Implement Playwright fallback for generic JavaScript-generated download links and page-triggered downloads.
- [ ] Enforce request/redirect policy for the complete browser request graph; do not rely on post-response peer checks as the only SSRF control.
- [ ] Bound navigation/download deadlines, output bytes, browser process lifetime, and cleanup on cancellation/failure.

### Worker and pipeline integration

- [ ] Isolate provider/browser recovery in a bounded worker process with a versioned request/result contract and atomic result writes.
- [ ] Validate staged outputs before promotion; ensure outputs are confined to the worker staging directory and remove temporary files on every exit path.
- [ ] Add integration coverage from IMAP URL discovery through candidate collection, recovery, extraction, pairing, and terminal result export.
- [ ] Register and exercise the complete application/infrastructure composition root, including extraction and archive stages when those implementations are introduced.

## Security invariants

- Raw URL/query tokens, email bodies, and raw exception text must not enter persisted identity, candidate metadata, traces, or user-facing failure messages.
- Every network redirect must be independently validated and connected only to a validated public IP address.
- Browser fallback must block unsafe requests before they are sent, not merely diagnose the connected peer afterward.
- Worker output paths must be validated before files are made visible to downstream stages.
