namespace InvoiceFlowAI.Contracts.Url;

public sealed record UrlErrorMatrix(
    string SchemaVersion,
    IReadOnlyList<UrlError> Errors);