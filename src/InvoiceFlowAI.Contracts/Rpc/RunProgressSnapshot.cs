namespace InvoiceFlowAI.Contracts.Rpc;

public sealed record RunProgressSnapshot(
    RunState RunState,
    bool IsRunning,
    bool CanStop,
    bool StopRequested,
    int Progress,
    string StatusText,
    RunProgressStats Stats,
    IReadOnlyList<RunLogEntry> Logs,
    string? LastError,
    bool QuotaExhausted,
    string? QuotaMessage,
    string? BuildIdentity,
    string? RawDateRange,
    string? ImapQueryRange)
{
    public IReadOnlyList<RunMailboxFetchFailureDiagnostic> MailboxFetchFailures { get; init; }
        = Array.Empty<RunMailboxFetchFailureDiagnostic>();
}