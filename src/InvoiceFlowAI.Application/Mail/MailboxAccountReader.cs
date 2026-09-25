using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.Application.Mail;

public sealed record MailboxConnectionSettings(
    string AccountId,
    string EmailAddress,
    string ImapHost,
    int ImapPort,
    bool UseTls,
    string CredentialName,
    string? DefaultMailbox);

public interface IMailboxAccountReader
{
    Task<IReadOnlyList<MailboxAccountSnapshot>> ListAsync(CancellationToken cancellationToken);

    Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken);
}