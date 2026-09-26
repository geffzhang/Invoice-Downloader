namespace InvoiceFlowAI.Application.Persistence;

public interface IManualReviewItemStore
{
    Task UpsertOpenAsync(
        string runId,
        string documentId,
        int processingRevision,
        string reasonCode,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}