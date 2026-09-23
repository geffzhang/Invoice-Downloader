namespace InvoiceFlowAI.Contracts.Rpc;

/// <summary>Payload for the <c>run.progress</c> event.</summary>
public sealed record RunProgressPayload(
    string Stage,
    int Completed,
    int Total,
    int Percent);