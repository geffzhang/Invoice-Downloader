// Compactable progress event record. Distinct from AuditEventRecord so
// retention rules can differ between the two streams.

namespace InvoiceFlowAI.Application.Persistence;

public sealed record StoredRunEventRecord(
    string RunId,
    long EventSequence,
    string EventType,
    string PayloadJson,
    DateTimeOffset EmittedAtUtc,
    DateTimeOffset? ExpiresAtUtc);