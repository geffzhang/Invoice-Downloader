// Checkpoint service: writes / refreshes / reads RunCheckpoints so the
// RunCoordinator can mark a packet as durable. A checkpoint is *only*
// updated inside the same UoW as the business-state write that produced it
// — there is no observable window in which the database can say a packet
// committed but no checkpoint row exists for it.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Errors;

namespace InvoiceFlowAI.Application.Runs;

public interface ICheckpointService
{
    Task RecordAsync(
        string runId,
        string nodeId,
        string stage,
        long lastCommittedSequence,
        int outputCount,
        CancellationToken cancellationToken);

    Task<RunCheckpointSnapshot?> LatestAsync(string runId, string nodeId, CancellationToken cancellationToken);

    Task<bool> ExceedsSnapshotThresholdAsync(string runId, string nodeId, int everyNPackets, CancellationToken cancellationToken);
}

public sealed class CheckpointService : ICheckpointService
{
    private readonly IRunCheckpointStore _store;
    private readonly IUnitOfWorkFactory _uowFactory;

    public CheckpointService(IRunCheckpointStore store, IUnitOfWorkFactory uowFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
    }

    public async Task RecordAsync(
        string runId,
        string nodeId,
        string stage,
        long lastCommittedSequence,
        int outputCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(stage);

        await using var uow = await _uowFactory.BeginAsync(TransactionPurpose.CheckpointWrite, cancellationToken).ConfigureAwait(false);
        var existing = await _store.ReadAsync(runId, nodeId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var snapshot = new RunCheckpointSnapshot(
            RunId: runId,
            NodeId: nodeId,
            Stage: stage,
            LastCommittedSequence: lastCommittedSequence,
            OutputCount: outputCount,
            State: existing?.State ?? "Active",
            CheckpointRevision: (existing?.CheckpointRevision ?? 0) + 1,
            UpdatedAtUtc: now);
        await _store.WriteAsync(snapshot, uow, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunCheckpointSnapshot?> LatestAsync(string runId, string nodeId, CancellationToken cancellationToken)
    {
        return await _store.ReadAsync(runId, nodeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ExceedsSnapshotThresholdAsync(string runId, string nodeId, int everyNPackets, CancellationToken cancellationToken)
    {
        if (everyNPackets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(everyNPackets), "everyNPackets must be > 0.");
        }
        var snapshot = await _store.ReadAsync(runId, nodeId, cancellationToken).ConfigureAwait(false);
        return snapshot is null || snapshot.CheckpointRevision % everyNPackets == 0;
    }
}

public sealed class CheckpointSnapshotRequiredException : Exception
{
    public CheckpointSnapshotRequiredException(string message, string reasonCode)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}