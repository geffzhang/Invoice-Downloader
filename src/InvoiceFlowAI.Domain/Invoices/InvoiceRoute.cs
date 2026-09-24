namespace InvoiceFlowAI.Domain.Invoices;

/// <summary>
/// Travel details and direction for an invoice. Static direction values
/// preserve the existing inbound/outbound construction pattern.
/// </summary>
public sealed record InvoiceRoute(
    InvoiceRouteDirection Direction,
    DateOnly? DepartureDate = null,
    string DepartureCity = "",
    string DestinationCity = "")
{
    public static InvoiceRoute Inbound { get; } = new(InvoiceRouteDirection.Inbound);
    public static InvoiceRoute Outbound { get; } = new(InvoiceRouteDirection.Outbound);
    public static InvoiceRoute Unknown { get; } = new(InvoiceRouteDirection.Unknown);
}

public enum InvoiceRouteDirection
{
    Inbound = 0,
    Outbound = 1,
    Unknown = 2,
}