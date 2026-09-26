using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.Application.Mail;

public interface IMailboxConnectionTester
{
    Task<AccountTestResult> TestAsync(
        MailboxConnectionSettings settings,
        string credential,
        string? mailbox,
        CancellationToken cancellationToken);
}