using System.Net.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.Application.Accounts;

public sealed class AccountApplicationService
{
    public const string ImapAuthCodeCredentialName = "mail.imap.auth-code";

    private readonly IMailboxAccountStore _accountStore;
    private readonly ISecretStore _secretStore;
    private readonly IUnitOfWorkFactory _uowFactory;

    public AccountApplicationService(
        IMailboxAccountStore accountStore,
        ISecretStore secretStore,
        IUnitOfWorkFactory uowFactory)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
    }

    public async Task<AccountListResult> ListAsync(CancellationToken cancellationToken)
    {
        var accounts = await _accountStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var ordered = new List<MailboxAccountSnapshot>(accounts.Count);

        foreach (var account in accounts.OrderBy(x => x.AccountId, StringComparer.Ordinal))
        {
            var configured = await IsCredentialConfiguredAsync(account.CredentialName, cancellationToken)
                .ConfigureAwait(false);
            ordered.Add(account with { CredentialConfigured = configured });
        }

        return new AccountListResult(ordered);
    }

    public async Task<MailboxAccountSnapshot> SaveAsync(
        AccountSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Account);
        Validate(request);

        var credentialConfigured = await IsCredentialConfiguredAsync(
            request.Account.CredentialName,
            cancellationToken).ConfigureAwait(false);

        await using var uow = await _uowFactory
            .BeginAsync(TransactionPurpose.MailboxUpsert, cancellationToken)
            .ConfigureAwait(false);

        var saved = await _accountStore.SaveAsync(
            request.Account,
            request.ExpectedRevision,
            uow,
            cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);

        return saved with { CredentialConfigured = credentialConfigured };
    }

    private async Task<bool> IsCredentialConfiguredAsync(string credentialName, CancellationToken cancellationToken)
    {
        if (!string.Equals(credentialName, ImapAuthCodeCredentialName, StringComparison.Ordinal))
        {
            return false;
        }

        var value = await _secretStore.GetAsync(credentialName, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void Validate(AccountSaveRequest request)
    {
        var account = request.Account;
        if (request.ExpectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "ExpectedRevision cannot be negative.");
        }

        if (account.AccountId is null || account.AccountId.Length > 64)
        {
            throw new ArgumentException("AccountId must be empty or at most 64 characters.", nameof(request));
        }

        if (!MailAddress.TryCreate(account.EmailAddress, out var parsedEmail)
            || !string.Equals(parsedEmail.Address, account.EmailAddress, StringComparison.Ordinal)
            || account.EmailAddress.Length > 256)
        {
            throw new ArgumentException("EmailAddress must be a valid email address.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(account.ImapHost)
            || account.ImapHost.Length > 256
            || !string.Equals(account.ImapHost, account.ImapHost.Trim(), StringComparison.Ordinal)
            || Uri.CheckHostName(account.ImapHost) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("ImapHost must be a host name or IP address, not a URL.", nameof(request));
        }

        if (account.ImapPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "ImapPort must be between 1 and 65535.");
        }

        if (!string.Equals(account.CredentialName, ImapAuthCodeCredentialName, StringComparison.Ordinal))
        {
            throw new ArgumentException("CredentialName is not an allowed mailbox credential reference.", nameof(request));
        }

        if (account.DisplayName is null || account.DisplayName.Length > 128)
        {
            throw new ArgumentException("DisplayName must be at most 128 characters.", nameof(request));
        }

        if (account.DefaultMailbox?.Length > 128)
        {
            throw new ArgumentException("DefaultMailbox must be at most 128 characters.", nameof(request));
        }
    }
}