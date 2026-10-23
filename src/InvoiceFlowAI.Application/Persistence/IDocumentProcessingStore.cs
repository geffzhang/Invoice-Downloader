using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Persistence;

/// <summary>
/// Terminal state of one document processing attempt. (DocumentId,
/// ProcessingRevision) is the idempotency key; repeated writes update the
/// row instead of creating a second processing result.
/// </summary>
public sealed record DocumentProcessingWrite(
    string DocumentId,
    int ProcessingRevision,
    string RunId,
    string SourceKind,
    string? SourceMessageUid,
    string? SourceFileName,
    string Stage,
    CandidateStatus Status,
    string ReasonCode,
    bool Retryable,
    int Attempt,
    int MaxAttempts,
    string? ArtifactPath,
    string? ResultJson,
    string? TraceJson,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public interface IDocumentProcessingStore
{
    Task UpsertAsync(
        DocumentProcessingWrite record,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}
