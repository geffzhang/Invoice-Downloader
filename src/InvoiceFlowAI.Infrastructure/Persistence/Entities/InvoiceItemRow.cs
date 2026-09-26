// Line item entity. Cascade-deletes with its parent InvoiceRow.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class InvoiceItemRow
{
    public string InvoiceItemId { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string? Name { get; set; }
    public string? Specification { get; set; }
    public string? Unit { get; set; }
    public string? Quantity { get; set; }
    public string? UnitPrice { get; set; }
    public string? Amount { get; set; }
    public string? TaxRate { get; set; }
    public string? TaxAmount { get; set; }
}