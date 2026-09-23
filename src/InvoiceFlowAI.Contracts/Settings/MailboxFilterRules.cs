namespace InvoiceFlowAI.Contracts.Settings;

/// <summary>Mailbox pre-filter rules applied before scanning.</summary>
public sealed record MailboxFilterRules(
    bool IncludeReadMessages,
    int? MinAttachmentBytes,
    int? MaxAttachmentBytes,
    IReadOnlyList<string>? SenderAllowList = null,
    IReadOnlyList<string>? SenderDenyList = null,
    IReadOnlyList<string>? SubjectAllowList = null);