// IRunCheckpointStore implementation. Writes through the caller's UoW so
// the checkpoint row commits atomically with the business state write and
// the RunEvent append.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfRunCheckpointStore : IRunCheckpointStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfRunCheckpointStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<RunCheckpointSnapshot?> ReadAsync(string runId, string nodeId, CancellationToken cancellationToken)
    {
        var row = await _context.RunCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RunId == runId && c.NodeId == nodeId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        return new RunCheckpointSnapshot(
            row.RunId,
            row.NodeId,
            row.Stage,
            row.LastCommittedSequence,
            row.OutputCount,
            row.State,
            row.CheckpointRevision,
            row.UpdatedAtUtc);
    }

    public async Task WriteAsync(RunCheckpointSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("RunCheckpointStore writes must use EfUnitOfWork.");
        }
        var row = await _context.RunCheckpoints
            .FirstOrDefaultAsync(c => c.RunId == snapshot.RunId && c.NodeId == snapshot.NodeId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            _context.RunCheckpoints.Add(new RunCheckpointRow
            {
                RunId = snapshot.RunId,
                NodeId = snapshot.NodeId,
                Stage = snapshot.Stage,
                LastCommittedSequence = snapshot.LastCommittedSequence,
                OutputCount = snapshot.OutputCount,
                State = snapshot.State,
                CheckpointRevision = snapshot.CheckpointRevision,
                UpdatedAtUtc = snapshot.UpdatedAtUtc,
            });
        }
        else
        {
            row.Stage = snapshot.Stage;
            row.LastCommittedSequence = snapshot.LastCommittedSequence;
            row.OutputCount = snapshot.OutputCount;
            row.State = snapshot.State;
            row.CheckpointRevision = snapshot.CheckpointRevision;
            row.UpdatedAtUtc = snapshot.UpdatedAtUtc;
        }
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RunCheckpointSnapshot>> ListAsync(string runId, CancellationToken cancellationToken)
    {
        var rows = await _context.RunCheckpoints
            .AsNoTracking()
            .Where(c => c.RunId == runId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => new RunCheckpointSnapshot(
            r.RunId, r.NodeId, r.Stage, r.LastCommittedSequence, r.OutputCount,
            r.State, r.CheckpointRevision, r.UpdatedAtUtc)).ToList();
    }
}