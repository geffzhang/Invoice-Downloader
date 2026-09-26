namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunErrorInvoiceResult(
    string? Date,
    string? Reason,
    string? Status,
    string? Merchant,
    string? Path);