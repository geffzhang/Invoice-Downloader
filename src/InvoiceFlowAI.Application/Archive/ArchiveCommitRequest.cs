// Application-layer contract for the archive two-phase commit coordinator.
// The contract is intentionally narrow so the implementation can enforce the
// Absent -> Prepared -> Committed lifecycle plus a RecoveryRequired escape
// hatch when evidence is ambiguous (design §7 / §11).

namespace InvoiceFlowAI.Application.Archive;

/// <summary>
/// Identity used for idempotency. A re-attempt with the same key is a no-op
/// provided the final file already exists with the same content hash;
/// a re-attempt with a different content hash is a hard failure.
/// </summary>
public sealed record ArchiveArtifactKey(
    string RunId,
    string DocumentId,
    int ProcessingRevision,
    string Role,
    string ExpectedContentHash);

public sealed record ArchiveCommitRequest(
    ArchiveArtifactKey Key,
    string TempFilePath,
    string FinalRelativePath,
    string FileName);

public sealed record ArchiveCommitResult(
    string ArtifactId,
    ArchiveArtifactState State,
    string FinalRelativePath,
    string ContentHash,
    bool AlreadyExisted);

public enum ArchiveArtifactState
{
    Absent = 0,
    Prepared = 1,
    Committed = 2,
    RecoveryRequired = 3,
}

/// <summary>
/// Recovery coordinator scans for rows stuck in <see cref="ArchiveArtifactState.Prepared"/>
/// after a process crash and decides whether the final file is present
/// (complete the commit), absent (re-prepare from temp), corrupt (orphan),
/// or both gone (evidence missing).
/// </summary>
public interface IArchiveCommitCoordinator
{
    Task<ArchiveCommitResult> CommitAsync(ArchiveCommitRequest request, CancellationToken cancellationToken);
}

public sealed record ArchiveRecoveryEntry(
    string ArtifactId,
    ArchiveArtifactKey Key,
    string TempFilePath,
    string FinalRelativePath,
    string FileName,
    string ExpectedContentHash,
    ArchiveArtifactState CurrentState);

public sealed record ArchiveRecoveryDecision(
    string ArtifactId,
    ArchiveArtifactState ResolvedState,
    string? ReasonCode,
    string? SafeMessage);

public interface IArchiveRecoveryService
{
    Task<IReadOnlyList<ArchiveRecoveryEntry>> ScanAsync(string runId, CancellationToken cancellationToken);

    Task<ArchiveRecoveryDecision> ResolveAsync(ArchiveRecoveryEntry entry, CancellationToken cancellationToken);
}