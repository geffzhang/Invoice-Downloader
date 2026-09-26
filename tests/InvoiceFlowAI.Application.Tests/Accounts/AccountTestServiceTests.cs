using FluentAssertions;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Accounts;

public sealed class AccountTestServiceTests
{
    [Fact]
    public async Task Test_mailbox_resolves_account_secret_and_calls_tester_once()
    {
        var account = NewConnectionSettings();
        var reader = new FakeAccountReader(account);
        var secrets = new FakeSecretStore(new Dictionary<string, string?>
        {
            ["mail.imap.auth-code"] = "private-auth-code",
        });
        var mailboxTester = new FakeMailboxConnectionTester();
        var providerTester = new FakeProviderConnectionTester();
        var service = new AccountTestService(reader, secrets, mailboxTester, providerTester);

        var result = await service.TestMailboxAsync(new AccountTestRequest("acct-1", "Invoices"), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        secrets.ReadNames.Should().Equal("mail.imap.auth-code");
        mailboxTester.CallCount.Should().Be(1);
        mailboxTester.LastSettings.Should().Be(account);
        mailboxTester.LastCredential.Should().Be("private-auth-code");
        mailboxTester.LastMailbox.Should().Be("Invoices");
        providerTester.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_mailbox_credential_fails_before_network_tester_is_called()
    {
        var secrets = new FakeSecretStore();
        var mailboxTester = new FakeMailboxConnectionTester();
        var service = new AccountTestService(
            new FakeAccountReader(NewConnectionSettings()),
            secrets,
            mailboxTester,
            new FakeProviderConnectionTester());

        var result = await service.TestMailboxAsync(new AccountTestRequest("acct-1"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be("CREDENTIALS_NOT_CONFIGURED");
        mailboxTester.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Test_provider_resolves_only_the_matching_deepseek_key()
    {
        var secrets = new FakeSecretStore(new Dictionary<string, string?>
        {
            ["deepseek.api-key"] = "private-provider-key",
            ["mail.imap.auth-code"] = "must-not-be-read",
        });
        var providerTester = new FakeProviderConnectionTester();
        var service = new AccountTestService(
            new FakeAccountReader(null),
            secrets,
            new FakeMailboxConnectionTester(),
            providerTester);

        var result = await service.TestProviderAsync(
            new ProviderTestRequest("deepseek", "deepseek.api-key"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        secrets.ReadNames.Should().Equal("deepseek.api-key");
        providerTester.CallCount.Should().Be(1);
        providerTester.LastCredential.Should().Be("private-provider-key");
    }

    [Fact]
    public async Task Provider_name_mismatch_does_not_read_secret_or_call_tester()
    {
        var secrets = new FakeSecretStore(new Dictionary<string, string?>
        {
            ["deepseek.api-key"] = "private-provider-key",
        });
        var providerTester = new FakeProviderConnectionTester();
        var service = new AccountTestService(
            new FakeAccountReader(null),
            secrets,
            new FakeMailboxConnectionTester(),
            providerTester);

        var result = await service.TestProviderAsync(
            new ProviderTestRequest("glm", "deepseek.api-key"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be("PROVIDER_CREDENTIAL_MISMATCH");
        secrets.ReadNames.Should().BeEmpty();
        providerTester.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Tester_exception_is_mapped_without_echoing_secret_or_exception_text()
    {
        const string secret = "private-auth-code";
        var mailboxTester = new FakeMailboxConnectionTester
        {
            Failure = new InvalidOperationException($"auth failed with {secret}"),
        };
        var service = new AccountTestService(
            new FakeAccountReader(NewConnectionSettings()),
            new FakeSecretStore(new Dictionary<string, string?> { ["mail.imap.auth-code"] = secret }),
            mailboxTester,
            new FakeProviderConnectionTester());

        var result = await service.TestMailboxAsync(new AccountTestRequest("acct-1"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be("CONNECTION_TEST_FAILED");
        result.SafeMessage.Should().NotContain(secret);
        result.SafeMessage.Should().NotContain("auth failed");
    }

    private static MailboxConnectionSettings NewConnectionSettings()
        => new("acct-1", "alice@example.com", "imap.example.com", 993, true, "mail.imap.auth-code", "INBOX");

    private sealed class FakeAccountReader(MailboxConnectionSettings? account) : IMailboxAccountReader
    {
        public Task<IReadOnlyList<MailboxAccountSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MailboxAccountSnapshot>>([]);

        public Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken)
            => Task.FromResult(account?.AccountId == accountId ? account : null);
    }

    private sealed class FakeSecretStore(IReadOnlyDictionary<string, string?>? values = null) : ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string?> _values = values ?? new Dictionary<string, string?>();
        public List<string> ReadNames { get; } = [];

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
        {
            ReadNames.Add(name);
            return Task.FromResult(_values.GetValueOrDefault(name));
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMailboxConnectionTester : IMailboxConnectionTester
    {
        public int CallCount { get; private set; }
        public MailboxConnectionSettings? LastSettings { get; private set; }
        public string? LastCredential { get; private set; }
        public string? LastMailbox { get; private set; }
        public Exception? Failure { get; init; }

        public Task<AccountTestResult> TestAsync(
            MailboxConnectionSettings settings,
            string credential,
            string? mailbox,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastSettings = settings;
            LastCredential = credential;
            LastMailbox = mailbox;
            if (Failure is not null)
            {
                return Task.FromException<AccountTestResult>(Failure);
            }

            return Task.FromResult(new AccountTestResult(settings.AccountId, true, mailbox ?? "INBOX", "Connected."));
        }
    }

    private sealed class FakeProviderConnectionTester : IProviderConnectionTester
    {
        public int CallCount { get; private set; }
        public string? LastCredential { get; private set; }

        public Task<ProviderTestResult> TestAsync(string providerId, string credential, CancellationToken cancellationToken)
        {
            CallCount++;
            LastCredential = credential;
            return Task.FromResult(new ProviderTestResult(providerId, true, SafeMessage: "Connected."));
        }
    }
}