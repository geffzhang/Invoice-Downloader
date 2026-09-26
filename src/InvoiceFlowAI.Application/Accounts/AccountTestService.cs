using System.Security.Cryptography;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Accounts;

public sealed class AccountTestService
{
    private const string DeepSeekProviderId = "deepseek";

    private readonly IMailboxAccountReader _accountReader;
    private readonly ISecretStore _secretStore;
    private readonly IMailboxConnectionTester _mailboxTester;
    private readonly IProviderConnectionTester _providerTester;

    public AccountTestService(
        IMailboxAccountReader accountReader,
        ISecretStore secretStore,
        IMailboxConnectionTester mailboxTester,
        IProviderConnectionTester providerTester)
    {
        _accountReader = accountReader ?? throw new ArgumentNullException(nameof(accountReader));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _mailboxTester = mailboxTester ?? throw new ArgumentNullException(nameof(mailboxTester));
        _providerTester = providerTester ?? throw new ArgumentNullException(nameof(providerTester));
    }

    public async Task<AccountTestResult> TestMailboxAsync(AccountTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("AccountId is required.", nameof(request));
        }

        var account = await _accountReader.FindAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new AccountTestResult(request.AccountId, false, request.Mailbox ?? "INBOX", "Mailbox account is unavailable.", "MAILBOX_ACCOUNT_NOT_FOUND");
        }

        if (!string.Equals(account.CredentialName, SecretApplicationService.ImapAuthCodeName, StringComparison.Ordinal))
        {
            return MissingMailboxCredential(account, request.Mailbox);
        }

        string? credential;
        try
        {
            credential = await _secretStore.GetAsync(account.CredentialName, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            return MissingMailboxCredential(account, request.Mailbox);
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            return MissingMailboxCredential(account, request.Mailbox);
        }

        var mailbox = string.IsNullOrWhiteSpace(request.Mailbox)
            ? string.IsNullOrWhiteSpace(account.DefaultMailbox) ? "INBOX" : account.DefaultMailbox
            : request.Mailbox.Trim();

        try
        {
            return await _mailboxTester.TestAsync(account, credential, mailbox, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AccountTestResult(account.AccountId, false, mailbox, "Mailbox connection test failed.", "CONNECTION_TEST_FAILED");
        }
    }

    public async Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ProviderId, DeepSeekProviderId, StringComparison.Ordinal)
            || !string.Equals(request.CredentialName, SecretApplicationService.DeepSeekApiKeyName, StringComparison.Ordinal))
        {
            return new ProviderTestResult(request.ProviderId, false, "PROVIDER_CREDENTIAL_MISMATCH", "Provider credential is not configured.");
        }

        string? credential;
        try
        {
            credential = await _secretStore.GetAsync(request.CredentialName, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            return MissingProviderCredential(request.ProviderId);
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            return MissingProviderCredential(request.ProviderId);
        }

        try
        {
            return await _providerTester.TestAsync(request.ProviderId, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProviderTestResult(request.ProviderId, false, "PROVIDER_TEST_FAILED", "Provider connection test failed.");
        }
    }

    private static AccountTestResult MissingMailboxCredential(MailboxConnectionSettings account, string? mailbox)
        => new(
            account.AccountId,
            false,
            string.IsNullOrWhiteSpace(mailbox) ? account.DefaultMailbox ?? "INBOX" : mailbox,
            "Mailbox credentials are unavailable. Enter the authorization code again.",
            RpcErrorCodes.CredentialsNotConfigured);

    private static ProviderTestResult MissingProviderCredential(string providerId)
        => new(providerId, false, RpcErrorCodes.CredentialsNotConfigured, "Provider credentials are unavailable. Enter the API key again.");
}