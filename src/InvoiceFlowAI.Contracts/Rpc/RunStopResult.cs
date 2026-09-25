namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunStopResult(
    bool Accepted,
    bool AlreadyRequested,
    string? ErrorCode);