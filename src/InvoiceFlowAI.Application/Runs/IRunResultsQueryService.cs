using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Runs;

public interface IRunResultsQueryService
{
    Task<RunResultsSnapshot> GetAsync(string? runId, CancellationToken cancellationToken);
}
