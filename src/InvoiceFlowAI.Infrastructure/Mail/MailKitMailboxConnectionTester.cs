using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Contracts.Accounts;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace InvoiceFlowAI.Infrastructure.Mail;

public sealed class MailKitMailboxConnectionTester : IMailboxConnectionTester
{
    private readonly IMailboxSessionFactory _sessionFactory;

    public MailKitMailboxConnectionTester(IMailboxSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    public async Task<AccountTestResult> TestAsync(
        MailboxConnectionSettings settings,
        string credential,
        string? mailbox,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(credential);
        var normalizedMailbox = string.IsNullOrWhiteSpace(mailbox)
            ? string.IsNullOrWhiteSpace(settings.DefaultMailbox) ? "INBOX" : settings.DefaultMailbox
            : mailbox.Trim();
        var session = _sessionFactory.Create();

        try
        {
            await session.ConnectAsync(settings, cancellationToken).ConfigureAwait(false);
            await session.AuthenticateAsync(settings.EmailAddress, credential, cancellationToken).ConfigureAwait(false);
            await session.OpenReadOnlyAsync(normalizedMailbox, cancellationToken).ConfigureAwait(false);
            return new AccountTestResult(settings.AccountId, true, normalizedMailbox, "Mailbox connection succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return Failure(settings.AccountId, normalizedMailbox, "IMAP_LOGIN_FAILED", "Mailbox login failed.");
        }
        catch (Exception exception) when (exception is ImapCommandException or ImapProtocolException)
        {
            return Failure(settings.AccountId, normalizedMailbox, "IMAP_PROTOCOL_FAILED", "Mailbox protocol test failed.");
        }
        catch
        {
            return Failure(settings.AccountId, normalizedMailbox, "IMAP_CONNECTION_FAILED", "Mailbox connection test failed.");
        }
        finally
        {
            try
            {
                await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static AccountTestResult Failure(string accountId, string mailbox, string code, string message)
        => new(accountId, false, mailbox, message, code);
}