using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class ProviderTestRpcHandler(AccountTestService service)
    : TypedRpcHandler<ProviderTestRequest, ProviderTestResult>("provider.test")
{
    protected override Task<ProviderTestResult> ExecuteAsync(ProviderTestRequest parameters, CancellationToken cancellationToken)
        => service.TestProviderAsync(parameters, cancellationToken);
}