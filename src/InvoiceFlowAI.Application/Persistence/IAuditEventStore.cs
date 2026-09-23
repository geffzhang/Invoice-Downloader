// Application abstraction for the append-only audit ledger. Concrete
// implementations (e.g. EfAuditStore) write through the same DbContext
// the caller passes via the UoW so the audit row commits atomically with
// the business state write.

namespace InvoiceFlowAI.Application.Persistence;

public interface IAuditEventStore
{
    Task AppendAsync(AuditEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken);
}