namespace InvoiceFlowAI.Domain.Invoices;

[System.Flags]
public enum InvoiceFlags
{
    None = 0,
    Itinerary = 1,
    Folio = 2,
    CreditNote = 4,
    Cancellation = 8,
    LowConfidence = 16,
    RequiresManualReview = 32,
}