using FluentAssertions;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Accounts;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Accounts;

public sealed class AccountApplicationServiceTests
{
    [Fact]
    public async Task List_returns_accounts_in_stable_order_with_secret_state_derived_from_store()
    {
        var store = new FakeMailboxAccountStore();
        store.Accounts.Add(Snapshot("acct-z", "mail.imap.auth-code"));
        store.Accounts.Add(Snapshot("acct-a", "mail.imap.auth-code"));
        var secrets = new FakeSecretStore(new Dictionary<string, string?>
        {
            ["mail.imap.auth-code"] = "configured-auth-code",
        });
        var service = CreateService(store, secrets, new FakeUnitOfWorkFactory());

        var result = await service.ListAsync(CancellationToken.None);

        result.Items.Select(x => x.AccountId).Should().Equal("acct-a", "acct-z");
        result.Items.Should().OnlyContain(x => x.CredentialConfigured);
    }

    [Fact]
    public async Task List_returns_empty_result_when_no_accounts_exist()
    {
        var service = CreateService(new FakeMailboxAccountStore(), new FakeSecretStore(), new FakeUnitOfWorkFactory());

        var result = await service.ListAsync(CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_creates_account_at_revision_zero_and_commits_mailbox_uow()
    {
        var store = new FakeMailboxAccountStore();
        var uowFactory = new FakeUnitOfWorkFactory();
        var secrets = new FakeSecretStore(new Dictionary<string, string?>
        {
            ["mail.imap.auth-code"] = "configured-auth-code",
        });
        var service = CreateService(store, secrets, uowFactory);

        var result = await service.SaveAsync(
            new AccountSaveRequest(Draft("acct-new"), ExpectedRevision: 0),
            CancellationToken.None);

        result.AccountId.Should().Be("acct-new");
        result.Revision.Should().Be(1);
        result.CredentialConfigured.Should().BeTrue();
        store.LastExpectedRevision.Should().Be(0);
        uowFactory.LastPurpose.Should().Be(TransactionPurpose.MailboxUpsert);
        uowFactory.LastUnitOfWork!.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task Save_passes_update_revision_to_store()
    {
        var store = new FakeMailboxAccountStore();
        var uowFactory = new FakeUnitOfWorkFactory();
        var service = CreateService(store, new FakeSecretStore(), uowFactory);

        await service.SaveAsync(
            new AccountSaveRequest(Draft("acct-existing"), ExpectedRevision: 7),
            CancellationToken.None);

        store.LastExpectedRevision.Should().Be(7);
    }

    [Fact]
    public async Task Save_propagates_revision_conflict_without_committing()
    {
        var store = new FakeMailboxAccountStore
        {
            SaveException = new MailboxAccountRevisionConflictException("acct-stale"),
        };
        var uowFactory = new FakeUnitOfWorkFactory();
        var service = CreateService(store, new FakeSecretStore(), uowFactory);

        var act = () => service.SaveAsync(
            new AccountSaveRequest(Draft("acct-stale"), ExpectedRevision: 2),
            CancellationToken.None);

        await act.Should().ThrowAsync<MailboxAccountRevisionConflictException>();
        uowFactory.LastUnitOfWork!.Committed.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://imap.example.com", "mail.imap.auth-code")]
    [InlineData("imap.example.com/path", "mail.imap.auth-code")]
    [InlineData("imap.example.com", "arbitrary.secret")]
    public async Task Save_rejects_malformed_endpoint_or_unrecognized_credential_name(
        string imapHost,
        string credentialName)
    {
        var store = new FakeMailboxAccountStore();
        var uowFactory = new FakeUnitOfWorkFactory();
        var service = CreateService(store, new FakeSecretStore(), uowFactory);

        var act = () => service.SaveAsync(
            new AccountSaveRequest(Draft("acct-invalid") with
            {
                ImapHost = imapHost,
                CredentialName = credentialName,
            }, ExpectedRevision: 0),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        store.SaveCalls.Should().Be(0);
        uowFactory.BeginCalls.Should().Be(0);
    }

    private static AccountApplicationService CreateService(
        FakeMailboxAccountStore store,
        FakeSecretStore secrets,
        FakeUnitOfWorkFactory uowFactory)
        => new(store, secrets, uowFactory);

    private static MailboxAccountDraft Draft(string accountId)
        => new(
            accountId,
            "alice@example.com",
            "imap.example.com",
            993,
            true,
            "mail.imap.auth-code",
            "Alice",
            "INBOX");

    private static MailboxAccountSnapshot Snapshot(string accountId, string credentialName)
        => new(
            accountId,
            "alice@example.com",
            "imap.example.com",
            993,
            true,
            credentialName,
            "Alice",
            1,
            false,
            "a***@example.com",
            DateTimeOffset.UtcNow,
            "INBOX");

    private sealed class FakeMailboxAccountStore : IMailboxAccountStore
    {
        public List<MailboxAccountSnapshot> Accounts { get; } = [];
        public int SaveCalls { get; private set; }
        public int? LastExpectedRevision { get; private set; }
        public Exception? SaveException { get; init; }

        public Task<IReadOnlyList<MailboxAccountSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MailboxAccountSnapshot>>(Accounts);

        public Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken)
            => Task.FromResult<MailboxConnectionSettings?>(null);

        public Task<MailboxAccountSnapshot> SaveAsync(
            MailboxAccountDraft draft,
            int expectedRevision,
            IUnitOfWork transaction,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            LastExpectedRevision = expectedRevision;
            if (SaveException is not null)
            {
                return Task.FromException<MailboxAccountSnapshot>(SaveException);
            }

            return Task.FromResult(Snapshot(draft.AccountId, draft.CredentialName) with
            {
                Revision = expectedRevision + 1,
            });
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string?> _values;

        public FakeSecretStore(IReadOnlyDictionary<string, string?>? values = null)
            => _values = values ?? new Dictionary<string, string?>();

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult(_values.GetValueOrDefault(name));

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeUnitOfWorkFactory : IUnitOfWorkFactory
    {
        public int BeginCalls { get; private set; }
        public TransactionPurpose? LastPurpose { get; private set; }
        public FakeUnitOfWork? LastUnitOfWork { get; private set; }

        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
        {
            BeginCalls++;
            LastPurpose = purpose;
            LastUnitOfWork = new FakeUnitOfWork(purpose);
            return Task.FromResult<IUnitOfWork>(LastUnitOfWork);
        }
    }

    private sealed class FakeUnitOfWork(TransactionPurpose purpose) : IUnitOfWork
    {
        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public TransactionPurpose Purpose { get; } = purpose;
        public bool IsCompleted => Committed;
        public bool Committed { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}