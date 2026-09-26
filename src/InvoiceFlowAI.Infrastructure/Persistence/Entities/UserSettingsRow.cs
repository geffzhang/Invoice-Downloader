// Singleton row in UserSettings. Per design §8 the first row is created
// by RuleSetBootstrapper inside the same bootstrap UoW so the FK to
// RuleSets is always valid.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class UserSettingsRow
{
    public string SettingsId { get; set; } = "default";
    public int Revision { get; set; }
    public string? CurrentAccountId { get; set; }
    public string DefaultMailbox { get; set; } = "INBOX";
    public string CompanyName { get; set; } = string.Empty;
    public string? LastOutputDirectory { get; set; }
    public string? MailboxFiltersJson { get; set; }
    public string? PipelineOptionsJson { get; set; }
    public bool AllowVisionFallback { get; set; }
    public string RuleSetId { get; set; } = "default";
    public int RuleSetVersion { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}