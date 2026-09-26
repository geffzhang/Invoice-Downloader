using InvoiceFlowAI.Application.Archive;

namespace InvoiceFlowAI.Application.Persistence;

public sealed record PairingCompanionRecord(string DocumentId, int ProcessingRevision);

public sealed record PairingRecord(
    string RunId,
    string InvoiceDocumentId,
    int InvoiceProcessingRevision,
    IReadOnlyList<PairingCompanionRecord> Companions,
    int TotalScore,
    string State,
    string ReasonCode);

public interface IPairingStore
{
    Task UpsertAsync(PairingRecord record, IUnitOfWork transaction, CancellationToken cancellationToken);

    Task ReconcileArchiveStateAsync(
        string runId,
        IReadOnlyList<ArchiveArtifactSnapshot> artifacts,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}