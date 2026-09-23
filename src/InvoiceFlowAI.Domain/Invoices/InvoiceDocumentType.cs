namespace InvoiceFlowAI.Domain.Invoices;

/// <summary>
/// Coarse classifier used by the archive naming policy and the report
/// schema. Values map 1:1 to the <c>InvoiceDocumentType</c> registry in
/// <c>rules/default.v1.json</c> — additions require a new schema version.
/// </summary>
public enum InvoiceDocumentType
{
    Unrecognized = 0,
    RideInvoice = 1,
    RideItinerary = 2,
    HotelInvoice = 3,
    HotelFolio = 4,
    TrainTicket = 5,
    AirTicket = 6,
    TaxInvoice = 7,
    VatInvoice = 8,
    Other = 99,
}