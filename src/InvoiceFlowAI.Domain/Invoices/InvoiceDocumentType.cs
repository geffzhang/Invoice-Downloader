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
    Catering = 100,
    Taxi = 101,
    AccommodationInvoice = 102,
    AccommodationStatement = 103,
    AccommodationConfirmation = 104,
    FlightTicket = 105,
    Itinerary = 106,
    TravelService = 107,
    Toll = 108,
    FixedInvoice = 109,
    NonTargetCompanyInvoice = 110,
    PersonalNonReimbursementInvoice = 111,
}