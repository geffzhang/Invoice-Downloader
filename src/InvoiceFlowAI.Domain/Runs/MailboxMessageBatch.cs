namespace InvoiceFlowAI.Domain.Runs;

/// <summary>Batched envelope of mailbox messages emitted by scan-mailbox.</summary>
public sealed record MailboxMessageBatch(
    string AccountId,
    string Mailbox,
    long BatchSequence,
    IReadOnlyList<MailboxMessage> Messages,
    bool IsFinalBatch,
    int ScannedCount);