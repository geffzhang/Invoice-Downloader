// Persistence entity for a per-node checkpoint. LastCommittedSequence
// captures the highest pipeline sequence whose packet has had its business
// write + audit + replay event committed in the same UoW.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class RunCheckpointRow
{
    public string RunId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public long LastCommittedSequence { get; set; }
    public string? InputCursorJson { get; set; }
    public int OutputCount { get; set; }
    public string State { get; set; } = string.Empty;
    public int CheckpointRevision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}