namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunInvoiceResult(
    string? Date,
    string? Amount,
    string? Merchant,
    string? Category,
    string? Path);