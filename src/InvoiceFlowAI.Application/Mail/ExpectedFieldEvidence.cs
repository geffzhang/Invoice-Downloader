namespace InvoiceFlowAI.Application.Mail;

public enum ExpectedFieldSource
{
    UrlQuery,
    Subject,
    Body,
}

public sealed record ExpectedFieldEvidence(
    string Value,
    ExpectedFieldSource Source,
    long DiscoveryOrder);