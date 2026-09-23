namespace InvoiceFlowAI.Contracts.EmailBody;

/// <summary>
/// Parsed receipt envelope. <c>Amount</c> is a decimal-as-string to preserve
/// the cents format used by the fixture; <c>InvoiceDate</c> uses ISO
/// <c>yyyy-MM-dd</c> per design spec §3.
/// </summary>
public sealed record EmailBodyReceiptResult(
    bool IsInvoice,
    DateOnly? InvoiceDate,
    string? InvoiceNumber,
    string? Purchaser,
    string? Seller,
    string? Amount,
    string? DocumentType);