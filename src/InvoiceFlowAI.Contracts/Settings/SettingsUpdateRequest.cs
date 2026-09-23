namespace InvoiceFlowAI.Contracts.Settings;

public sealed record SettingsUpdateRequest(
    int ExpectedRevision,
    string? AccountId = null,
    string? Mailbox = null,
    MailboxFilterRules? MailboxFilters = null,
    PipelineOptionsPatch? Pipeline = null,
    string? CustomRuleSetJson = null,
    bool? AllowVisionFallback = null);