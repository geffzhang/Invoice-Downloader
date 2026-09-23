// Persistence entity for a mailbox account. Authorization codes are
// NEVER stored on this row; CredentialName references a logical secret
// name saved by ISecretStore. Revision drives optimistic concurrency.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class MailboxAccountRow
{
    public string AccountId { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; }
    public bool UseTls { get; set; }
    public string CredentialName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}