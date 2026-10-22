using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.Application.Accounts;

public interface IAccountApplicationService
{
    Task<AccountListResult> ListAsync(CancellationToken cancellationToken);
    Task<MailboxAccountSnapshot> SaveAsync(AccountSaveRequest request, CancellationToken cancellationToken);
}
