namespace InvoiceFlowAI.Domain.Invoices;

/// <summary>
/// Whether the document represents an inbound purchase (purchaser pays) or
/// outbound sale (seller charges). Used to decide which side of the
/// purchase-sale match the document should fill in pairing.
/// </summary>
public enum InvoiceRoute
{
    Inbound = 0,
    Outbound = 1,
    Unknown = 2,
}