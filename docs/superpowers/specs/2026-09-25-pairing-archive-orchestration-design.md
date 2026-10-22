# Pairing and Archive Orchestration Design

## Goal

Complete the native .NET pairing-to-archive path while preserving the Python pairing behavior and every source artifact. The work adds document-level pairing review explanations, a production `IDocumentArchivingStage`, durable pairing state, and recoverable archive orchestration.

## Compatibility Contract

`PairingEngine` remains behaviorally compatible with `pairing_engine.py`:

- Ride and hotel compatibility checks, tolerances, provider handling, and edge-score weights do not change.
- Assignment maximizes pair count first and total edge score second.
- A connected component with multiple optimal pair memberships remains ambiguous; the engine must not choose by input order or document ID.
- Output order stays deterministic under shuffled input.

Tests use shared golden cases and focused Python-reference parity vectors. This task does not retune weights or replace the assignment algorithm.

## Pairing Review Reasons

The pairing result gains a deterministic per-document explanation for each unmatched eligible document. Review codes are:

- `PAIRING_COUNTERPART_MISSING`: no document with the opposite role exists in the same pairing group.
- `PAIRING_NO_COMPATIBLE_EDGE`: opposite-role documents exist, but none pass the existing compatibility rules. The explanation may include stable evidence codes for provider, amount, and date incompatibility.
- `PAIRING_AMBIGUOUS_OPTIMUM`: the document belongs to a connected component with multiple optimal assignments.
- `PAIRING_UNMATCHED_BY_GLOBAL_ASSIGNMENT`: at least one compatible edge exists, but the unique global optimum leaves this document unmatched.

Ambiguity takes precedence over the generic unmatched classification for every document in that component. `ArtifactPairingStage` maps these codes to candidate-level `ManualReview` failures with safe messages and no source text, merchant text, or filesystem paths. Matched documents and documents outside supported pairing families retain their existing status.

## Archive Orchestration

Add a production `IDocumentArchivingStage` implementation and register it in Infrastructure DI. It consumes `PairingBatch`, preserves candidate sequence, and produces one archive result for each input candidate. It uses the existing two-phase `ArchiveCommitCoordinator`, recovery service, and archive naming policy rather than introducing another commit protocol.

### Source Preservation

`CandidateProcessResult.ArtifactPath` is an immutable source path. It must never be passed to `AtomicMoveAsync`, renamed, truncated, or deleted by archiving.

Before preparing a commit, the archive filesystem creates a unique temporary copy beside the destination on the same volume. The coordinator hashes the copy and compares it with the expected source content hash. The `Prepared` snapshot records the copy path. Phase B atomically moves only that archive-owned copy to the final path and verifies the final hash.

Before the `Prepared` transaction commits, a failed attempt may clean up its private copy. After `Prepared` commits, the copy or final file is recovery evidence and must not be deleted by the stage. Recovery operates only on the recorded archive-owned copy and destination. Source-preservation tests assert that the original file remains present with identical bytes after success, idempotent retry, hash mismatch, interruption, and recovery.

### File and Pair Lifecycle

- Resolved paired artifacts use deterministic adjacent ride/hotel filenames compatible with the Python naming rules.
- Other resolved artifacts use deterministic invoice naming.
- Manual-review and retained artifacts are preserved under their corresponding review/retained paths, carrying stable reason codes in result and persistence metadata.
- Unresolved or authentication-failed artifacts are preserved as review evidence when a source path exists; otherwise the candidate remains unresolved with a sanitized failure.
- Duplicate outcomes do not create another archive copy.
- A pair is considered committed only when all of its required artifacts are committed. If only one artifact commits, retain it, leave pairing state recoverable, and resume idempotently; do not roll back or delete committed evidence.

Pairing persistence uses `PairingRow` with the run, document/revision identities, companion identities/revisions, assignment score, state, and reason code. Pairing writes must be deterministic and idempotent. Per-document archive artifacts continue to use `ArchiveArtifactKey` idempotency.

## Failure Semantics

- Candidate-specific path, file, or content failures produce sanitized candidate failures and do not abort unrelated candidates.
- Cancellation propagates and does not convert to a candidate failure.
- Transaction, persistence, or recovery infrastructure failures are reported in `ArchiveBatch.Failures` while preserving `Prepared` evidence for recovery.
- Hash mismatch fails closed. The source stays untouched; no mismatched copy is committed.
- Path collision is never overwritten silently; it produces a stable archive collision reason and leaves source evidence intact.
- No network access is added to pairing or archiving.

## Testing and Acceptance

1. Pairing parity: all existing Python-compatible cases, score objective ordering, connected components, ambiguity, and shuffled-input determinism remain unchanged.
2. Review classification: counterpart missing, no compatible edge with evidence, ambiguous optimum, and globally unmatched compatible document map to distinct stable codes; ambiguity precedence is tested.
3. Archive stage: supported statuses, output ordering, paired naming, per-candidate isolation, DI resolution, and result path propagation are covered.
4. Source preservation: the source remains byte-identical through successful commits, retries, hash failures, collisions, simulated crashes, and recovery.
5. Durability: prepared-copy recovery, final-file recovery, partial pair commit, idempotent retry, and audit/pairing state transitions are tested.
6. Gates: focused Pairing and Archive suites, full solution tests, Release build, Windows x64 publish, and final diff/privacy review pass.

## Non-Goals

- Changing Python pairing weights, compatibility tolerances, or the optimization objective.
- Moving or deleting original `ArtifactPath` files.
- Refactoring unrelated extraction, mailbox scanning, or URL recovery behavior.
- Introducing runtime downloads or external services.