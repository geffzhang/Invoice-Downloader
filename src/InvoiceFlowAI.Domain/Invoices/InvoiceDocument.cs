namespace InvoiceFlowAI.Domain.Invoices;

/// <summary>
/// Structured, identity-preserving representation of an invoice. The
/// <see cref="InvoiceDate"/> is the business date, not the file write
/// date; the <see cref="SourceFileName"/> and <see cref="ContentHash"/>
/// are stable inputs to the archive naming policy and idempotency key.
/// </summary>
public sealed record InvoiceDocument(
    string DocumentId,
    DateOnly? InvoiceDate,
    string Purchaser,
    string Seller,
    decimal? Amount,
    decimal? TaxAmount,
    decimal? TotalAmount,
    string? InvoiceCode,
    string? InvoiceNumber,
    InvoiceDocumentType DocumentType,
    string? Category,
    InvoiceRoute Route,
    IReadOnlyList<InvoiceItem> Items,
    string SourceFileName,
    string ContentHash,
    IReadOnlyDictionary<string, string>? Trace = null);