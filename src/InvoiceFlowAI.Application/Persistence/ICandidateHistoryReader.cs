using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Persistence;

public interface ICandidateHistoryReader
{
    Task<bool> ExistsAsync(DocumentIdentity identity, CancellationToken cancellationToken);
}