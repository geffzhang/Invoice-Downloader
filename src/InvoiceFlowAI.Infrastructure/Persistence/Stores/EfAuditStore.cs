// IAuditStore implementation. Append-only writes; reads bypass the EF
// change tracker to avoid accidental mutations.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfAuditStore : IAuditEventStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfAuditStore(InvoiceFlowDbContext context) => _context = context;

    public async Task AppendAsync(AuditEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("AuditStore writes must use EfUnitOfWork.");
        }

        var eventSequence = record.EventSequence;
        if (eventSequence == 0)
        {
            var persistedMaximum = await _context.AuditEvents
                .Where(row => row.RunId == record.RunId)
                .Select(row => (long?)row.EventSequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0;
            var pendingMaximum = _context.ChangeTracker.Entries<AuditEventRow>()
                .Where(entry => entry.State == EntityState.Added && entry.Entity.RunId == record.RunId)
                .Select(entry => entry.Entity.EventSequence)
                .DefaultIfEmpty(0)
                .Max();
            eventSequence = Math.Max(persistedMaximum, pendingMaximum) + 1;
        }

        _context.AuditEvents.Add(new AuditEventRow
        {
            AuditEventId = record.AuditEventId,
            RunId = record.RunId,
            EventSequence = eventSequence,
            EventType = record.EventType,
            Stage = record.Stage,
            NodeId = record.NodeId,
            DocumentId = record.DocumentId,
            ProcessingRevision = record.ProcessingRevision,
            ReasonCode = record.ReasonCode,
            PayloadJson = record.PayloadJson,
            PayloadHash = record.PayloadHash,
            OccurredAtUtc = record.OccurredAtUtc,
        });
        await Task.CompletedTask;
    }
}