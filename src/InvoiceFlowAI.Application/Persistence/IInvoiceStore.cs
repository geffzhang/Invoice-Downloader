using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Persistence;

public interface IInvoiceStore
{
    Task UpsertAsync(
        InvoiceDocument invoice,
        int processingRevision,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}
