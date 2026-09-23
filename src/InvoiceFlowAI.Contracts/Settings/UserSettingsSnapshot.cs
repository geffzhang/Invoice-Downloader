namespace InvoiceFlowAI.Contracts.Settings;

/// <summary>
/// Server-emitted view of the singleton user settings. The fingerprint is
/// computed from non-secret fields only — secrets live in DPAPI.
/// </summary>
public sealed record UserSettingsSnapshot(
    int Revision,
    string? CurrentAccountId,
    string DefaultMailbox,
    MailboxFilterRules MailboxFilters,
    PipelineOptionsPatch Pipeline,
    bool AllowVisionFallback,
    string RuleSetId,
    int RuleSetVersion,
    string ConfigurationFingerprint,
    DateTimeOffset UpdatedAtUtc);