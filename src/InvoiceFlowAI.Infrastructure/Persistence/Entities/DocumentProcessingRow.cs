// Persistence entity for a single processing attempt of a document.
// (DocumentId, ProcessingRevision) is the idempotency key — repeated
// delivery of the same sequence is a no-op rather than a duplicate
// invoice, archive or audit entry.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class DocumentProcessingRow
{
    public string DocumentId { get; set; } = string.Empty;
    public int ProcessingRevision { get; set; }
    public string RunId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public bool Retryable { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    public string? ArtifactPath { get; set; }
    public string? ResultJson { get; set; }
    public string? TraceJson { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}