// IMailboxAccountStore implementation. Owns the optimistic-concurrency
// contract: SaveAsync with a stale expectedRevision throws
// MailboxAccountRevisionConflictException (mapped to
// MAILBOX_ACCOUNT_REVISION_CONFLICT by the dispatcher).

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfMailboxAccountStore : IMailboxAccountStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfMailboxAccountStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<MailboxAccountSnapshot> SaveAsync(
        MailboxAccountDraft draft,
        int expectedRevision,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("MailboxAccountStore writes must use EfUnitOfWork.");
        }

        var existing = await _context.MailboxAccounts
            .FirstOrDefaultAsync(x => x.AccountId == draft.AccountId, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            if (expectedRevision != 0)
            {
                throw new MailboxAccountRevisionConflictException(draft.AccountId ?? string.Empty);
            }

            var accountId = string.IsNullOrEmpty(draft.AccountId) ? Guid.NewGuid().ToString("N") : draft.AccountId;
            var created = new MailboxAccountRow
            {
                AccountId = accountId,
                EmailAddress = draft.EmailAddress,
                ImapHost = draft.ImapHost,
                ImapPort = draft.ImapPort,
                UseTls = draft.UseTls,
                CredentialName = draft.CredentialName,
                DisplayName = draft.DisplayName,
                DefaultMailbox = draft.DefaultMailbox,
                Revision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            _context.MailboxAccounts.Add(created);
            return new MailboxAccountSnapshot(
                accountId,
                draft.EmailAddress,
                draft.ImapHost,
                draft.ImapPort,
                draft.UseTls,
                draft.CredentialName,
                draft.DisplayName,
                1,
                false,
                MaskEmail(draft.EmailAddress),
                now,
                draft.DefaultMailbox);
        }

        if (existing.Revision != expectedRevision)
        {
            throw new MailboxAccountRevisionConflictException(existing.AccountId);
        }

        existing.EmailAddress = draft.EmailAddress;
        existing.ImapHost = draft.ImapHost;
        existing.ImapPort = draft.ImapPort;
        existing.UseTls = draft.UseTls;
        existing.CredentialName = draft.CredentialName;
        existing.DisplayName = draft.DisplayName;
        existing.DefaultMailbox = draft.DefaultMailbox;
        existing.Revision++;
        existing.UpdatedAtUtc = now;

        return new MailboxAccountSnapshot(
            existing.AccountId,
            existing.EmailAddress,
            existing.ImapHost,
            existing.ImapPort,
            existing.UseTls,
            existing.CredentialName,
            existing.DisplayName,
            existing.Revision,
            false,
            MaskEmail(existing.EmailAddress),
            existing.UpdatedAtUtc,
            existing.DefaultMailbox);
    }

    public Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken)
    {
        return _context.MailboxAccounts
            .AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .Select(x => new MailboxConnectionSettings(
                x.AccountId,
                x.EmailAddress,
                x.ImapHost,
                x.ImapPort,
                x.UseTls,
                x.CredentialName,
                x.DefaultMailbox))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxAccountSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.MailboxAccounts
            .AsNoTracking()
            .OrderBy(x => x.AccountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(x => new MailboxAccountSnapshot(
            x.AccountId,
            x.EmailAddress,
            x.ImapHost,
            x.ImapPort,
            x.UseTls,
            x.CredentialName,
            x.DisplayName ?? string.Empty,
            x.Revision,
            CredentialConfigured: false,
            MaskEmail(x.EmailAddress),
            x.UpdatedAtUtc,
            x.DefaultMailbox)).ToList();
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***";
        return string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }
}