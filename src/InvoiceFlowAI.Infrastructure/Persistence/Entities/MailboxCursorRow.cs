// Persistence entity for mailbox scan cursors. (AccountId, Mailbox) is
// the primary key. When UidValidity changes LastCompletedUid is reset to 0.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class MailboxCursorRow
{
    public string AccountId { get; set; } = string.Empty;
    public string Mailbox { get; set; } = string.Empty;
    public long UidValidity { get; set; }
    public long LastCompletedUid { get; set; }
    public int CursorRevision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}