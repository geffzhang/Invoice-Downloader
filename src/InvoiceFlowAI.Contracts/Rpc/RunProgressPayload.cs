namespace InvoiceFlowAI.Contracts.Rpc;

/// <summary>Payload for the <c>run.progress</c> event.</summary>
public sealed record RunProgressPayload(
    string Stage,
    int Completed,
    int Total,
    int Percent,
    RunProgressStats? Stats = null,
    string? LastError = null,
    bool QuotaExhausted = false,
    string? QuotaMessage = null)
{
    public IReadOnlyList<RunMailboxFetchFailureDiagnostic> MailboxFetchFailures { get; init; }
        = Array.Empty<RunMailboxFetchFailureDiagnostic>();
}