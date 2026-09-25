namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunGroupedErrorResult(
    string Key,
    string? Label,
    int Count,
    IReadOnlyList<RunErrorInvoiceResult> Items);