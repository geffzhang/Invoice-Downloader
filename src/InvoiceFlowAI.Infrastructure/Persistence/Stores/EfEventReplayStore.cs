// IEventReplayStore implementation. Append-only within a run, but the
// retention policy allows compact()-driven deletes; EF tracks changes
// only for inserts so callers can only add rows.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfEventReplayStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfEventReplayStore(InvoiceFlowDbContext context) => _context = context;

    public async Task AppendAsync(StoredRunEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("EventReplayStore writes must use EfUnitOfWork.");
        }

        _context.RunEvents.Add(new RunEventRow
        {
            RunId = record.RunId,
            EventSequence = record.EventSequence,
            EventType = record.EventType,
            PayloadJson = record.PayloadJson,
            EmittedAtUtc = record.EmittedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
        });
        await Task.CompletedTask;
    }

    public async Task<EventReplayResultRecord> ReadSinceAsync(string runId, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        var rows = await _context.RunEvents
            .AsNoTracking()
            .Where(e => e.RunId == runId && e.EventSequence > afterSequence)
            .OrderBy(e => e.EventSequence)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var events = rows
            .Select(r => new StoredRunEventRecord(r.RunId, r.EventSequence, r.EventType, r.PayloadJson, r.EmittedAtUtc, r.ExpiresAtUtc))
            .ToList();
        var latest = events.Count == 0 ? afterSequence : events[^1].EventSequence;

        return new EventReplayResultRecord(events, LatestSequence: latest, RequiresFullRefresh: false);
    }
}

public sealed record EventReplayResultRecord(
    IReadOnlyList<StoredRunEventRecord> Events,
    long LatestSequence,
    bool RequiresFullRefresh);