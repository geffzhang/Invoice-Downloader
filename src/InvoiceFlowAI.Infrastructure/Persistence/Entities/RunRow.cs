// Persistence entity for a Run. One row per run; terminal status snapshots
// (state, summary, primary failure) are persisted in the same row.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class RunRow
{
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string TerminalReasonCode { get; set; } = string.Empty;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateToExclusive { get; set; }
    public string? AccountId { get; set; }
    public int? AccountRevision { get; set; }
    public string? Mailbox { get; set; }
    public string? OutputRoot { get; set; }
    public int? SettingsRevision { get; set; }
    public string? RuleSetId { get; set; }
    public int? RuleSetVersion { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public string? RecipeVersion { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public long LastEventSequence { get; set; }
    public string? SummaryJson { get; set; }
    public string? PrimaryFailureJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}