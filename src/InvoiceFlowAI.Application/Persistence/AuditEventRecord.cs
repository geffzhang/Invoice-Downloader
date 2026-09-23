// Audit-event payload record. Lives in Application so the Infrastructure
// adapter can map it to/from the EF row without leaking SQL types up.

namespace InvoiceFlowAI.Application.Persistence;

public sealed record AuditEventRecord(
    string AuditEventId,
    string RunId,
    long EventSequence,
    string EventType,
    string Stage,
    string NodeId,
    string? DocumentId,
    int? ProcessingRevision,
    string ReasonCode,
    string PayloadJson,
    string PayloadHash,
    DateTimeOffset OccurredAtUtc);