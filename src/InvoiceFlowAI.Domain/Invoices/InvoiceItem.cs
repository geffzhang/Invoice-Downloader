namespace InvoiceFlowAI.Domain.Invoices;

/// <summary>
/// Line item on an invoice. Amount / tax use decimal to preserve cent
/// precision; quantity uses decimal to allow fractional units.
/// </summary>
public sealed record InvoiceItem(
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    decimal? TaxAmount = null,
    string? Unit = null,
    string? Specification = null);