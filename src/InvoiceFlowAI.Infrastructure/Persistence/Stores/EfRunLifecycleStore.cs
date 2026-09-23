// IRunLifecycleStore implementation. Reads the active RunRow and updates
// its terminal state + LastEventSequence cursor in the caller's UoW so
// the cursor advance commits atomically with each packet.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfRunLifecycleStore : IRunLifecycleStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfRunLifecycleStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<RunStateSnapshot?> FindAsync(string runId, CancellationToken cancellationToken)
    {
        var row = await _context.Runs
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        return new RunStateSnapshot(
            row.RunId,
            MapState(row.State),
            row.Stage,
            row.TerminalReasonCode,
            row.LastEventSequence,
            row.EndedAtUtc,
            row.CancellationRequestedAtUtc);
    }

    public async Task UpdateTerminalStateAsync(RunStateSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("RunLifecycleStore writes must use EfUnitOfWork.");
        }
        var row = await _context.Runs
            .FirstOrDefaultAsync(r => r.RunId == snapshot.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException($"Run '{snapshot.RunId}' not found for terminal update.");
        }
        row.State = snapshot.State.ToString();
        row.Stage = snapshot.Stage;
        row.TerminalReasonCode = snapshot.TerminalReasonCode ?? "";
        row.EndedAtUtc = snapshot.EndedAtUtc;
        if (snapshot.LastEventSequence > row.LastEventSequence)
        {
            row.LastEventSequence = snapshot.LastEventSequence;
        }
        await Task.CompletedTask;
    }

    public async Task UpdateLastEventSequenceAsync(string runId, long lastEventSequence, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("RunLifecycleStore writes must use EfUnitOfWork.");
        }
        var row = await _context.Runs
            .FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException($"Run '{runId}' not found for sequence update.");
        }
        if (lastEventSequence > row.LastEventSequence)
        {
            row.LastEventSequence = lastEventSequence;
        }
        await Task.CompletedTask;
    }

    public async Task RequestCancellationAsync(string runId, DateTimeOffset requestedAtUtc, IUnitOfWork? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null)
        {
            // outside UoW — direct context write
            var row = await _context.Runs.FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
            if (row is null) throw new InvalidOperationException($"Run '{runId}' not found for cancellation.");
            row.CancellationRequestedAtUtc = requestedAtUtc;
            return;
        }
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("RunLifecycleStore writes must use EfUnitOfWork.");
        }
        var tracked = await _context.Runs.FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
        if (tracked is null) throw new InvalidOperationException($"Run '{runId}' not found for cancellation.");
        tracked.CancellationRequestedAtUtc = requestedAtUtc;
        await Task.CompletedTask;
    }

    public async Task<bool> IsCancellationRequestedAsync(string runId, CancellationToken cancellationToken)
    {
        var row = await _context.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
        return row is not null && row.CancellationRequestedAtUtc is not null;
    }

    private static RunLifecycleState MapState(string state) => state switch
    {
        "Completed" => RunLifecycleState.Completed,
        "PartialSuccess" => RunLifecycleState.PartialSuccess,
        "NeedsManualReview" => RunLifecycleState.NeedsManualReview,
        "Cancelled" => RunLifecycleState.Cancelled,
        "Failed" => RunLifecycleState.Failed,
        "Recovering" => RunLifecycleState.Recovering,
        _ => RunLifecycleState.Running,
    };
}