using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.App.Rpc;

public sealed class AccountSaveRpcHandler(AccountApplicationService service)
    : TypedRpcHandler<AccountSaveRequest, MailboxAccountSnapshot>("account.save")
{
    protected override Task<MailboxAccountSnapshot> ExecuteAsync(AccountSaveRequest parameters, CancellationToken cancellationToken)
        => service.SaveAsync(parameters, cancellationToken);
}