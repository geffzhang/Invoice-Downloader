// Verifies MailboxAccounts optimistic concurrency and connection-setting reads:
//   * SaveAsync with stale expectedRevision returns MAILBOX_ACCOUNT_REVISION_CONFLICT
//   * FindAsync returns non-secret settings including the persisted default mailbox

using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class MailboxAccountRevisionTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public MailboxAccountRevisionTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Save_with_stale_expected_revision_returns_conflict()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfMailboxAccountStore(context);

        await using (var uow = await BeginAsync(context))
        {
            var snapshot = await store.SaveAsync(
                NewDraft("acct-1"),
                expectedRevision: 0,
                uow,
                CancellationToken.None);
            snapshot.Revision.Should().Be(1);
            await uow.CommitAsync(CancellationToken.None);
        }

        var second = NewDraft("acct-1") with { DisplayName = "Alice (renamed)" };

        await using var uow2 = await BeginAsync(context);
        var act = () => store.SaveAsync(second, expectedRevision: 0, uow2, CancellationToken.None);
        await act.Should().ThrowAsync<MailboxAccountRevisionConflictException>()
            .Where(e => e.ReasonCode == RpcErrorCodes.MailboxAccountRevisionConflict);
    }

    [Fact]
    public async Task Save_with_correct_expected_revision_succeeds()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfMailboxAccountStore(context);

        await using (var uow = await BeginAsync(context))
        {
            var snapshot = await store.SaveAsync(
                NewDraft("acct-2"),
                expectedRevision: 0,
                uow,
                CancellationToken.None);
            snapshot.Revision.Should().Be(1);
            await uow.CommitAsync(CancellationToken.None);
        }

        await using var uow2 = await BeginAsync(context);
        var update = NewDraft("acct-2") with { DisplayName = "Alice (renamed)" };
        var updated = await store.SaveAsync(update, expectedRevision: 1, uow2, CancellationToken.None);
        updated.Revision.Should().Be(2);
        updated.DisplayName.Should().Be("Alice (renamed)");
        await uow2.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FindAsync_returns_saved_connection_settings_including_default_mailbox()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfMailboxAccountStore(context);

        await using (var uow = await BeginAsync(context))
        {
            await store.SaveAsync(
                NewDraft("acct-read") with { DefaultMailbox = "Invoices" },
                expectedRevision: 0,
                uow,
                CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        IMailboxAccountReader reader = store;
        var settings = await reader.FindAsync("acct-read", CancellationToken.None);

        settings.Should().NotBeNull();
        settings!.AccountId.Should().Be("acct-read");
        settings.EmailAddress.Should().Be("alice@example.com");
        settings.ImapHost.Should().Be("imap.example.com");
        settings.ImapPort.Should().Be(993);
        settings.UseTls.Should().BeTrue();
        settings.CredentialName.Should().Be("mail.imap.auth-code");
        settings.DefaultMailbox.Should().Be("Invoices");
    }

    [Fact]
    public async Task FindAsync_returns_null_for_missing_account_id()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        IMailboxAccountReader reader = new EfMailboxAccountStore(context);

        var settings = await reader.FindAsync("missing", CancellationToken.None);

        settings.Should().BeNull();
    }

    [Fact]
    public async Task Save_update_persists_new_default_mailbox_for_subsequent_reads()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfMailboxAccountStore(context);

        await using (var uow = await BeginAsync(context))
        {
            await store.SaveAsync(
                NewDraft("acct-default") with { DefaultMailbox = "Archive" },
                expectedRevision: 0,
                uow,
                CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        await using (var uow = await BeginAsync(context))
        {
            var updated = await store.SaveAsync(
                NewDraft("acct-default") with { DefaultMailbox = "Invoices" },
                expectedRevision: 1,
                uow,
                CancellationToken.None);
            updated.DefaultMailbox.Should().Be("Invoices");
            await uow.CommitAsync(CancellationToken.None);
        }

        IMailboxAccountReader reader = store;
        var settings = await reader.FindAsync("acct-default", CancellationToken.None);

        settings.Should().NotBeNull();
        settings!.DefaultMailbox.Should().Be("Invoices");
    }

    private static MailboxAccountDraft NewDraft(string id) => new(
        AccountId: id,
        EmailAddress: "alice@example.com",
        ImapHost: "imap.example.com",
        ImapPort: 993,
        UseTls: true,
        CredentialName: "mail.imap.auth-code",
        DisplayName: "Alice");

    private static async Task<IUnitOfWork> BeginAsync(InvoiceFlowDbContext context)
    {
        var factory = new EfUnitOfWorkFactory(context);
        return await factory.BeginAsync(TransactionPurpose.MailboxUpsert, CancellationToken.None);
    }
}