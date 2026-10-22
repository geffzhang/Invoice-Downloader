# Provider, Archive, and CWT Parity Design

## Goal

Close the remaining provider recovery/security, archive recovery reconciliation, and CWT cancellation-matching parity gaps without reintroducing a Python runtime. Preserve observable business outcomes while using existing C# application contracts, durable stores, safe reason codes, and synthetic-only fixtures.

## Scope

1. Provider recovery and security
   - Exercise Nuonuo, Baiwang, and direct invoice provider strategies through deterministic fake transports; no live network calls.
   - Assert artifact kind/signature selection, provider identity matching, retries and safe failure outcomes for malformed/empty responses.
   - Assert public URL policy on every redirect hop, DNS-to-peer binding, configured proxy attestation, bounded responses, response disposal after read failures, and redaction in persisted diagnostics/log projections.
   - Keep credentials, signed query strings, real mail data, and resolved private paths out of fixtures and user-visible errors.
2. Archive recovery reconciliation
   - Use synthetic prepared artifact/pair snapshots for final-present, temp-only, neither-present, and hash-mismatch states.
   - Assert atomic restore/commit behavior, non-destructive recovery-required behavior when evidence is missing or inconsistent, persisted hashes, audit event, and pairing state reconciliation from the resulting artifact snapshots.
3. CWT cancellation matching
   - Preserve the minimal message sender/subject signal needed by CWT classification in the in-memory candidate pipeline; do not persist additional message content solely for matching.
   - Apply the existing CWT cancellation filename rule and register cancellation artifacts for one post-archive matching pass after ordinary archive/pair reconciliation.
   - Match every eligible hotel-confirmation artifact in the current run's archive inventory whose source filename contains the extracted name and hotel marker, using a deterministic, bounded filename parser.
   - Move matched confirmations to the existing manual-review path, update their archive artifact path/state through the archive store contract, and open a manual-review item with reason `CWT_CANCELLATION_MATCH`.
   - Write a relationship sidecar containing stable document/artifact identifiers, reason, and timestamp; do not include raw source URLs, absolute temporary paths, or unrelated mailbox content.
   - Make retries idempotent and verify archive artifact plus pairing reconciliation state. Do not add category/history mutations that the Python behavior does not perform.

## Boundaries and Failure Handling

- Provider/security tests exercise existing URL policies, transports, strategies, and selectors; production changes are limited to gaps demonstrated by failing tests.
- Archive recovery remains fail-closed: uncertain or mismatching evidence yields `RecoveryRequired`; no automatic deletion of user files.
- CWT classification relies on the CWT sender signal associated with the candidate message and the cancellation filename. A malformed filename, missing archive inventory, or failed move must not mark an unrelated artifact as matched; failures use stable safe codes and remain reviewable. Multiple matching hotel confirmations are all processed in deterministic candidate order, matching Python behavior.
- No secrets, source URL queries, raw exception text, or machine-specific absolute paths are included in public results or durable diagnostic payloads.

## Acceptance

- Focused provider strategy/security tests cover the matrix's listed redirect, proxy, bounded-body, cleanup, and redaction edges with deterministic fakes.
- Archive recovery fixtures prove state/hash/pairing reconciliation for success and failure branches.
- CWT fixtures prove source-signal propagation, registration-before-finalization, deterministic match, one-time move, idempotent retry, safe sidecar contents, and consistent archive/review/pairing state.
- Matrix rows change to `verified` only when the corresponding observable behavior has a focused passing C# test; any semantics without an existing Python contract remain explicitly marked as a decision.
- Focused .NET tests pass. No commit is created.
