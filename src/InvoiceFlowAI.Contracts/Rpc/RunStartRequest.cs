namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunStartRequest(
    string RunId,
    string AccountId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string OutputDirectory,
    string CompanyName,
    string RunMode);