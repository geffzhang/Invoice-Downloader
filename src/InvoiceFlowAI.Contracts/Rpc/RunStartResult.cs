namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunStartResult(
    bool Accepted,
    string RunId,
    string? RejectionCode);