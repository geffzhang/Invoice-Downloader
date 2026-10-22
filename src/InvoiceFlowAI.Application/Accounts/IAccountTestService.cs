using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Accounts;

public interface IAccountTestService
{
    Task<AccountTestResult> TestMailboxAsync(AccountTestRequest request, CancellationToken cancellationToken);
    Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken);
}
