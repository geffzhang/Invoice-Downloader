// Application-level abstraction over the compactable run-event store. The
// RunEvents table is append-only within a run but may be compacted by the
// retention service, so reads expose a RequiresFullRefresh hint when the
// caller must re-load from sequence 0 because the compacted range included
// events that have since been removed (design §8).

namespace InvoiceFlowAI.Application.Persistence;

public interface IEventReplayStore
{
    Task AppendAsync(StoredRunEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken);

    Task<EventReplayResultRecord> ReadSinceAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record EventReplayResultRecord(
    IReadOnlyList<StoredRunEventRecord> Events,
    long LatestSequence,
    bool RequiresFullRefresh);