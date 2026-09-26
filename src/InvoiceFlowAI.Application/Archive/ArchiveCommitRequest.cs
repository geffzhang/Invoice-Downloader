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
    string SourceFilePath,
    string FinalFilePath,
    string FinalRelativePath,
    string FileName,
    string? SourceFileName = null);

public sealed record ArchiveCommitResult(
    string ArtifactId,
    ArchiveArtifactState State,
    string FinalRelativePath,
    string ContentHash,
    bool AlreadyExisted,
    string? ReasonCode = null);

public sealed class ArchivePathCollisionException : IOException
{
    public const string StableReasonCode = "ARCHIVE_PATH_COLLISION";

    public ArchivePathCollisionException(string path)
        : base($"Archive destination already exists: {path}")
    {
    }
}

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
    ArchiveArtifactState CurrentState,
    string? FinalFilePath = null);

public sealed record ArchiveRecoveryDecision(
    string ArtifactId,
    ArchiveArtifactState ResolvedState,
    string? ReasonCode,
    string? SafeMessage);

public sealed record LegacyArchiveRecoveryDecision(
    string InventoryId,
    LegacyArchiveInventoryState ResolvedState,
    string? ReasonCode);

public sealed record ArchiveStartupRecoveryResult(
    IReadOnlyList<ArchiveRecoveryDecision> Artifacts,
    IReadOnlyList<LegacyArchiveRecoveryDecision> LegacyItems,
    int SkippedRootCount = 0);

public interface IArchiveRecoveryService
{
    Task<ArchiveStartupRecoveryResult> ReconcileAllKnownRootsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveRecoveryEntry>> ScanAsync(string runId, CancellationToken cancellationToken);

    Task<ArchiveRecoveryDecision> ResolveAsync(ArchiveRecoveryEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<LegacyArchiveRecoveryDecision>> ReconcileLegacyAsync(
        string outputRoot,
        CancellationToken cancellationToken);

    Task<ArchiveStartupRecoveryResult> ReconcileBeforeRunAsync(
        string outputRoot,
        CancellationToken cancellationToken);
}