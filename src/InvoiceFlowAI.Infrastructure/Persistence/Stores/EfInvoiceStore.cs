using System.Globalization;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfInvoiceStore : IInvoiceStore
{
    private const string PendingArchiveState = "Pending";

    private readonly InvoiceFlowDbContext _context;

    public EfInvoiceStore(InvoiceFlowDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task UpsertAsync(
        InvoiceDocument invoice,
        int processingRevision,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("InvoiceStore writes must use EfUnitOfWork.");
        }
        ArgumentException.ThrowIfNullOrEmpty(invoice.DocumentId);
        ArgumentOutOfRangeException.ThrowIfNegative(processingRevision);

        // FK_Invoices_DocumentProcessing is enforced at INSERT time; flush the
        // processing rows staged earlier in this transaction first because EF
        // does not order inserts between unrelated entity types.
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var invoiceId = invoice.DocumentId;
        var row = await _context.Invoices
            .SingleOrDefaultAsync(candidate => candidate.InvoiceId == invoiceId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new InvoiceRow { InvoiceId = invoiceId };
            _context.Invoices.Add(row);
        }

        row.DocumentId = invoiceId;
        row.ProcessingRevision = processingRevision;
        row.InvoiceDate = invoice.InvoiceDate ?? default;
        row.Purchaser = invoice.Purchaser ?? string.Empty;
        row.Seller = invoice.Seller ?? string.Empty;
        row.Amount = Money(invoice.Amount);
        row.TaxAmount = Money(invoice.TaxAmount);
        row.TotalAmount = Money(invoice.TotalAmount);
        row.InvoiceCode = invoice.InvoiceCode;
        row.InvoiceNumber = invoice.InvoiceNumber;
        row.DocumentType = invoice.DocumentType.ToString();
        row.Category = invoice.Category;
        row.Flags = (int)invoice.Flags;
        row.Confidence = invoice.Confidence.ToString(CultureInfo.InvariantCulture);
        row.ArchiveState = PendingArchiveState;

        // Flush the invoice before staging its line items for the same
        // FK-ordering reason (FK_InvoiceItems_Invoices).
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var existing = (await _context.InvoiceItems
                .Where(item => item.InvoiceId == invoiceId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(item => item.Ordinal);

        for (var ordinal = 0; ordinal < invoice.Items.Count; ordinal++)
        {
            var item = invoice.Items[ordinal];
            if (!existing.Remove(ordinal, out var itemRow))
            {
                itemRow = new InvoiceItemRow
                {
                    InvoiceItemId = $"{invoiceId}-{ordinal}",
                    InvoiceId = invoiceId,
                    Ordinal = ordinal,
                };
                _context.InvoiceItems.Add(itemRow);
            }

            itemRow.Name = item.Name;
            itemRow.Specification = item.Specification;
            itemRow.Unit = item.Unit;
            itemRow.Quantity = item.Quantity.ToString(CultureInfo.InvariantCulture);
            itemRow.UnitPrice = item.UnitPrice.ToString(CultureInfo.InvariantCulture);
            itemRow.Amount = item.Amount.ToString(CultureInfo.InvariantCulture);
            itemRow.TaxAmount = item.TaxAmount?.ToString(CultureInfo.InvariantCulture);
        }

        _context.InvoiceItems.RemoveRange(existing.Values);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Money(decimal? value) =>
        (value ?? 0m).ToString(CultureInfo.InvariantCulture);
}
