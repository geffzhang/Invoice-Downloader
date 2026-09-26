// Persistence entity for an invoice. Money columns are stored as invariant
// decimal text — never as floating point. DuplicateKey is the user-friendly
// composite hash that the UI uses to surface repeat invoices, and is the
// primary dedupe boundary when (DocumentId, ProcessingRevision) differs.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class InvoiceRow
{
    public string InvoiceId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public int ProcessingRevision { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public string Purchaser { get; set; } = string.Empty;
    public string Seller { get; set; } = string.Empty;
    public string Amount { get; set; } = "0";
    public string TaxAmount { get; set; } = "0";
    public string TotalAmount { get; set; } = "0";
    public string? InvoiceCode { get; set; }
    public string? InvoiceNumber { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int Flags { get; set; }
    public string Confidence { get; set; } = "0";
    public string? DuplicateKey { get; set; }
    public string ArchiveState { get; set; } = "Pending";
    public int Revision { get; set; }
}