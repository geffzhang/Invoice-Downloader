// Application-level event-replay facade. Responsible for detecting gaps in
// the per-run event sequence so the RunCoordinator can abort if a packet is
// missing. Per design §5 the (RunId, EventSequence) pair is unique and
// append-only; a gap is treated as a run failure (REPLAY_GAP) rather than
// silently filled.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Errors;

namespace InvoiceFlowAI.Application.Runs;

public interface IEventReplayService
{
    Task<EventReplayGap?> DetectGapAsync(string runId, CancellationToken cancellationToken);

    Task<RunEventReplaySlice> ReadSliceAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record EventReplayGap(long MissingFromSequence, long LowerBoundSequence, long HighBoundSequence, string ReasonCode);

public sealed record RunEventReplaySlice(IReadOnlyList<StoredRunEventRecord> Events, long LatestSequence, bool RequiresFullRefresh);

public sealed class EventReplayService : IEventReplayService
{
    private readonly IEventReplayStore _store;

    public EventReplayService(IEventReplayStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<EventReplayGap?> DetectGapAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        const int pageSize = 500;
        var previous = 0L;
        var page = await _store.ReadSinceAsync(runId, afterSequence: 0, pageSize, cancellationToken).ConfigureAwait(false);
        while (page.Events.Count > 0)
        {
            for (var i = 0; i < page.Events.Count; i++)
            {
                var expected = previous + 1;
                if (page.Events[i].EventSequence != expected)
                {
                    return new EventReplayGap(
                        MissingFromSequence: expected,
                        LowerBoundSequence: previous,
                        HighBoundSequence: page.Events[i].EventSequence,
                        ReasonCode: RpcErrorCodes.ReplayGap);
                }
                previous = page.Events[i].EventSequence;
            }
            if (page.Events.Count < pageSize) break;
            page = await _store.ReadSinceAsync(runId, afterSequence: previous, pageSize, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<RunEventReplaySlice> ReadSliceAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        var result = await _store.ReadSinceAsync(runId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
        return new RunEventReplaySlice(result.Events, result.LatestSequence, result.RequiresFullRefresh);
    }
}