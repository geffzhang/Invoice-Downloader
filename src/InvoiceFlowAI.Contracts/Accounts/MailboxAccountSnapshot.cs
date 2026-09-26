namespace InvoiceFlowAI.Contracts.Accounts;

/// <summary>
/// Server-emitted view of a mailbox account. <c>Revision</c> drives
/// optimistic concurrency for <c>account.save</c>; <c>MaskedEmailAddress</c>
/// and <c>CredentialConfigured</c> are the only credential-adjacent fields
/// the UI is allowed to see.
/// </summary>
public sealed record MailboxAccountSnapshot(
    string AccountId,
    string EmailAddress,
    string ImapHost,
    int ImapPort,
    bool UseTls,
    string CredentialName,
    string DisplayName,
    int Revision,
    bool CredentialConfigured,
    string MaskedEmailAddress,
    DateTimeOffset UpdatedAtUtc,
    string? DefaultMailbox = null);