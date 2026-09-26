using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfManualReviewItemStore : IManualReviewItemStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfManualReviewItemStore(InvoiceFlowDbContext context) => _context = context;

    public async Task UpsertOpenAsync(
        string runId,
        string documentId,
        int processingRevision,
        string reasonCode,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("ManualReviewItemStore writes must use EfUnitOfWork.");
        }
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        ArgumentOutOfRangeException.ThrowIfNegative(processingRevision);
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);

        var existing = await _context.ManualReviewItems.FirstOrDefaultAsync(row =>
            row.RunId == runId
            && row.DocumentId == documentId
            && row.ProcessingRevision == processingRevision
            && row.State == "Open",
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.ReasonCode = reasonCode;
            return;
        }

        _context.ManualReviewItems.Add(new ManualReviewItemRow
        {
            ReviewId = Guid.NewGuid().ToString("N"),
            RunId = runId,
            DocumentId = documentId,
            ProcessingRevision = processingRevision,
            ReasonCode = reasonCode,
            State = "Open",
            CurrentRevision = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
    }
}