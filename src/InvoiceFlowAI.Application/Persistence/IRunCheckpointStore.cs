// Application-level abstraction for the per-run checkpoint store. A
// checkpoint is the durable boundary the RunCoordinator relies on to
// recover from crash-after-commit / crash-before-commit ambiguity. The
// last-committed sequence is the cursor anchor for event replay.

using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Persistence;

public interface IRunCheckpointStore
{
    Task<RunCheckpointSnapshot?> ReadAsync(string runId, string nodeId, CancellationToken cancellationToken);

    Task WriteAsync(RunCheckpointSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunCheckpointSnapshot>> ListAsync(string runId, CancellationToken cancellationToken);
}

/// <summary>
/// Application-visible checkpoint snapshot. Maps to the RunCheckpoints row
/// but never exposes SQLite JSON blobs to callers.
/// </summary>
public sealed record RunCheckpointSnapshot(
    string RunId,
    string NodeId,
    string Stage,
    long LastCommittedSequence,
    int OutputCount,
    string State,
    int CheckpointRevision,
    DateTimeOffset UpdatedAtUtc);