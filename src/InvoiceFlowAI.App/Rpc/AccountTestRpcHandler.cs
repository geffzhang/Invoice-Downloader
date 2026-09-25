using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.App.Rpc;

public sealed class AccountTestRpcHandler(AccountTestService service)
    : TypedRpcHandler<AccountTestRequest, AccountTestResult>("account.test")
{
    protected override Task<AccountTestResult> ExecuteAsync(AccountTestRequest parameters, CancellationToken cancellationToken)
        => service.TestMailboxAsync(parameters, cancellationToken);
}