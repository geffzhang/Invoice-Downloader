namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunCandidateEventPayload(
    string DocumentId,
    string Status,
    string? ReasonCode);