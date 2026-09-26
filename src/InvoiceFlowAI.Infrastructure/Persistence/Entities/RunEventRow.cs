// Persistence entity for RunEvents. Distinct from AuditEventRows because
// RunEvents are progress events that may be compacted under the retention
// policy; the audit ledger must stay append-only forever.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class RunEventRow
{
    public string RunId { get; set; } = string.Empty;
    public long EventSequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset EmittedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}