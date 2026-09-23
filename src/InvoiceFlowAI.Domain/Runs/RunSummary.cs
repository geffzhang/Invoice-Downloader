using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Domain.Runs;

/// <summary>
/// Final, post-barrier summary of a run. Counts are produced exactly once
/// from the candidate result set and do not change after the run enters a
/// terminal state.
/// </summary>
public sealed record RunSummary(
    string RunId,
    RunTerminalStatus TerminalStatus,
    string TerminalReasonCode,
    int ResolvedCount,
    int DuplicateCount,
    int RetainedCount,
    int ManualReviewCount,
    int UnresolvedCount,
    int CancelledCount,
    int QuotaExhaustedCount,
    int AuthFailedCount,
    int TimeoutCount,
    IReadOnlyDictionary<string, int> FailureReasonCounts,
    string? ReportPath,
    string? ReportContentHash,
    DateTimeOffset CompletedAtUtc,
    bool ReportExportQueued,
    IReadOnlyList<RunFailure> FinalizerFailures);