using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.Application.Persistence;

public interface IMailboxAccountStore : IMailboxAccountReader
{
    Task<MailboxAccountSnapshot> SaveAsync(
        MailboxAccountDraft draft,
        int expectedRevision,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}