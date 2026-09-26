// IRunLifecycleStore implementation. Reads the active RunRow and updates
// its terminal state + LastEventSequence cursor in the caller's UoW so
// the cursor advance commits atomically with each packet.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfRunLifecycleStore : IRunLifecycleStore
{
    private static readonly string[] ActiveStates = { "Created", "Running", "Recovering" };
    private readonly InvoiceFlowDbContext _context;

    public EfRunLifecycleStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<IReadOnlyList<string>> ListOutputRootsAsync(CancellationToken cancellationToken)
        => await _context.Runs.AsNoTracking()
            .Where(run => run.OutputRoot != null && run.OutputRoot != string.Empty)
            .Select(run => run.OutputRoot!)
            .Distinct()
            .OrderBy(root => root)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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
            row.CancellationRequestedAtUtc,
            row.SummaryJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<RunSummary>(row.SummaryJson));
    }

    public async Task<bool> TryCreateAsync(
        RunCreationRequest request,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("RunLifecycleStore writes must use EfUnitOfWork.");
        }
        if (request.DateToExclusive <= request.DateFrom)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The run date range must be non-empty.");
        }

        var hasActiveRun = await _context.Runs
            .AnyAsync(run => ActiveStates.Contains(run.State), cancellationToken)
            .ConfigureAwait(false);
        if (hasActiveRun)
        {
            return false;
        }

        _context.Runs.Add(new RunRow
        {
            RunId = request.RunId,
            State = "Created",
            Stage = "admitted",
            DateFrom = request.DateFrom,
            DateToExclusive = request.DateToExclusive,
            AccountId = request.AccountId,
            AccountRevision = request.AccountRevision,
            Mailbox = request.Mailbox,
            OutputRoot = request.OutputRoot,
            SettingsRevision = request.SettingsRevision,
            RuleSetId = request.RuleSetId,
            RuleSetVersion = request.RuleSetVersion,
            ConfigurationFingerprint = request.ConfigurationFingerprint,
            RecipeVersion = request.RecipeVersion,
            StartedAtUtc = request.StartedAtUtc,
            CreatedAtUtc = request.StartedAtUtc,
        });
        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> TryMarkRunningAsync(
        string runId,
        string stage,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("RunLifecycleStore writes must use EfUnitOfWork.");
        }

        var row = _context.Runs.Local.FirstOrDefault(run => run.RunId == runId)
            ?? await _context.Runs.FirstOrDefaultAsync(run => run.RunId == runId, cancellationToken)
                .ConfigureAwait(false);
        if (row is null || row.State != "Created" || row.CancellationRequestedAtUtc is not null)
        {
            return false;
        }

        row.State = "Running";
        row.Stage = stage;
        return true;
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
        if (snapshot.Summary is not null)
        {
            row.SummaryJson = System.Text.Json.JsonSerializer.Serialize(snapshot.Summary);
        }
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

    public async Task<bool> TryRequestCancellationAsync(
        string runId,
        DateTimeOffset requestedAtUtc,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("RunLifecycleStore writes must use EfUnitOfWork.");
        }
        var tracked = await _context.Runs.FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
        if (tracked is null
            || !ActiveStates.Contains(tracked.State)
            || tracked.CancellationRequestedAtUtc is not null)
        {
            return false;
        }

        tracked.CancellationRequestedAtUtc = requestedAtUtc;
        return true;
    }

    public async Task<bool> IsCancellationRequestedAsync(string runId, CancellationToken cancellationToken)
    {
        var row = await _context.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
        return row is not null && row.CancellationRequestedAtUtc is not null;
    }

    private static RunLifecycleState MapState(string state) => state switch
    {
        "Created" => RunLifecycleState.Created,
        "Running" => RunLifecycleState.Running,
        "Completed" => RunLifecycleState.Completed,
        "PartialSuccess" => RunLifecycleState.PartialSuccess,
        "NeedsManualReview" => RunLifecycleState.NeedsManualReview,
        "Cancelled" => RunLifecycleState.Cancelled,
        "Failed" => RunLifecycleState.Failed,
        "Recovering" => RunLifecycleState.Recovering,
        _ => RunLifecycleState.Running,
    };
}