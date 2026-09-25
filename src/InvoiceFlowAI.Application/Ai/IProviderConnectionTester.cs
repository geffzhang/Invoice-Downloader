using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Ai;

public interface IProviderConnectionTester
{
    Task<ProviderTestResult> TestAsync(
        string providerId,
        string credential,
        CancellationToken cancellationToken);
}