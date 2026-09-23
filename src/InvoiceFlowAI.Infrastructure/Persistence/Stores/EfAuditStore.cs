// IAuditStore implementation. Append-only writes; reads bypass the EF
// change tracker to avoid accidental mutations.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfAuditStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfAuditStore(InvoiceFlowDbContext context) => _context = context;

    public async Task AppendAsync(AuditEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("AuditStore writes must use EfUnitOfWork.");
        }

        _context.AuditEvents.Add(new AuditEventRow
        {
            AuditEventId = record.AuditEventId,
            RunId = record.RunId,
            EventSequence = record.EventSequence,
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