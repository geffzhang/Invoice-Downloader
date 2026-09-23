// Persistence entity for the append-only AuditEvents table. The
// migration installs an UPDATE/DELETE trigger that raises SQLITE_CONSTRAINT
// to enforce immutability. Indexed by (RunId, EventSequence) and by
// (DocumentId, ProcessingRevision, EventType) for run/UI lookups.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class AuditEventRow
{
    public string AuditEventId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public long EventSequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string? DocumentId { get; set; }
    public int? ProcessingRevision { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string PayloadHash { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}