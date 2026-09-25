using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.App.Rpc;

public sealed class AccountListRpcHandler(AccountApplicationService service)
    : TypedRpcHandler<RpcEmptyParams, AccountListResult>("account.list", allowNullParams: true)
{
    protected override Task<AccountListResult> ExecuteAsync(RpcEmptyParams parameters, CancellationToken cancellationToken)
        => service.ListAsync(cancellationToken);
}