namespace InvoiceFlowAI.Domain.Runs;

/// <summary>
/// Minimal projection of an IMAP message header used by the
/// <c>scan-mailbox</c> node. The full <c>MailKit</c> object stays in the
/// Infrastructure layer; the domain sees only the fields that the candidate
/// collector and pairing rules need.
/// </summary>
public sealed record MailboxMessage(
    string Mailbox,
    string Uid,
    long UidValidity,
    DateTimeOffset? SentAtUtc,
    string Subject,
    string FromAddress,
    IReadOnlyList<string> AttachmentNames,
    bool HasInlineAttachments);