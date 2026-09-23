// Persistence entity for a manual-review queue item. Only one open review
// is allowed per (RunId, DocumentId, ProcessingRevision) — enforced by
// a partial unique index. CurrentRevision drives optimistic concurrency.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class ManualReviewItemRow
{
    public string ReviewId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public int ProcessingRevision { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int CurrentRevision { get; set; }
    public string? CurrentResultJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? ResolvedBy { get; set; }
}