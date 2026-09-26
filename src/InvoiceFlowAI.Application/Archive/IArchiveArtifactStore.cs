// Application abstraction for the ArchivedArtifacts table. The coordinator
// and recovery service talk to the store, never to EF directly, so tests
// can inject in-memory fakes while production runs through the EF store.

using InvoiceFlowAI.Application.Persistence;

namespace InvoiceFlowAI.Application.Archive;

public interface IArchiveArtifactStore
{
    Task<ArchiveArtifactSnapshot?> FindByKeyAsync(ArchiveArtifactKey key, CancellationToken cancellationToken);

    Task InsertPreparedAsync(ArchiveArtifactSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken);

    Task MarkCommittedAsync(string artifactId, DateTimeOffset committedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken);

    Task MarkRecoveryRequiredAsync(string artifactId, string reasonCode, IUnitOfWork transaction, CancellationToken cancellationToken);

    Task UpdateCommittedLocationAsync(
        string artifactId,
        string relativePath,
        string finalPath,
        string fileName,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListByRunAsync(string runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListCommittedForInventoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListRecoverableAsync(CancellationToken cancellationToken);
}

public sealed record ArchiveArtifactSnapshot(
    string ArtifactId,
    ArchiveArtifactKey Key,
    string TempFilePath,
    string FinalRelativePath,
    string FileName,
    string ExpectedContentHash,
    ArchiveArtifactState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CommittedAtUtc,
    string? FinalFilePath = null,
    string? SourceFileName = null,
    string? DocumentType = null);