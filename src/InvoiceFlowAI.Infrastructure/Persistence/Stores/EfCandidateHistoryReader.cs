using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfCandidateHistoryReader : ICandidateHistoryReader
{
    private readonly InvoiceFlowDbContext _context;

    public EfCandidateHistoryReader(InvoiceFlowDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public Task<bool> ExistsAsync(DocumentIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var documentIds = _context.Documents
            .AsNoTracking()
            .Where(document => document.DocumentId == identity.Value || document.ProviderGroupKey == identity.Value)
            .Select(document => document.DocumentId);
        return _context.DocumentProcessing
            .AsNoTracking()
            .AnyAsync(
                row => documentIds.Contains(row.DocumentId)
                    && row.CompletedAtUtc != null
                    && !row.Retryable,
                cancellationToken);
    }
}