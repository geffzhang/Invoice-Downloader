// Implementation of ITerminalDecisionService. Decides the run's terminal
// status using the priority order in design §5:
//   Failed > Cancelled > NeedsManualReview > PartialSuccess > Completed
// plus the auxiliary rules:
//   * Finalizer failures elevate a Completed run to Failed (terminal
//     barrier reached but a non-critical finalizer died).
//   * A run with no candidates and no failure is Completed only when the
//     caller signals allCandidatesArrived=true; otherwise it stays as a
//     phantom "no candidates observed yet" — the coordinator treats this
//     as a barrier guard.

using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Runs;

public sealed class TerminalDecisionService : ITerminalDecisionService
{
    public RunTerminalDecision Decide(
        string runId,
        IReadOnlyList<CandidateProcessResult> candidates,
        RunFailure? runFailure,
        bool cancellationRequested,
        bool allCandidatesArrived,
        IReadOnlyList<RunFailure> finalizerFailures,
        DateTimeOffset completedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(finalizerFailures);

        var counts = CountCandidates(candidates);
        var reasonCounts = CountReasons(candidates);

        var hasRunFailure = runFailure is not null;
        var hasCriticalFinalizer = finalizerFailures.Any(f =>
            f.Category is FailureCategory.Persistence or FailureCategory.Internal);
        var hasAnyCandidate = candidates.Count > 0;

        // Final barrier is reached when either:
        //   * The producer signalled end-of-stream (allCandidatesArrived), or
        //   * A run-level failure already exists (we must persist failure now), or
        //   * A critical finalizer failure already exists (must persist Failed).
        // Without one of these we cannot yet decide the run's terminal state.
        var barrierReached = allCandidatesArrived
            || hasRunFailure
            || hasCriticalFinalizer;

        if (!barrierReached)
        {
            return BuildDeferred(runId, counts, reasonCounts, finalizerFailures, completedAtUtc);
        }

        var status =
            hasRunFailure ? RunTerminalStatus.Failed :
            cancellationRequested ? RunTerminalStatus.Cancelled :
            hasCriticalFinalizer ? RunTerminalStatus.Failed :
            counts.GetValueOrDefault(CandidateStatus.ManualReview, 0) > 0
                ? RunTerminalStatus.NeedsManualReview :
            counts.GetValueOrDefault(CandidateStatus.Unresolved, 0) > 0 ||
            counts.GetValueOrDefault(CandidateStatus.Timeout, 0) > 0 ||
            counts.GetValueOrDefault(CandidateStatus.QuotaExhausted, 0) > 0 ||
            counts.GetValueOrDefault(CandidateStatus.AuthFailed, 0) > 0
                ? RunTerminalStatus.PartialSuccess :
                RunTerminalStatus.Completed;

        var reasonCode = status switch
        {
            RunTerminalStatus.Failed when hasCriticalFinalizer
                => RunTerminalReasonCodes.FinalizerFailed,
            RunTerminalStatus.Failed when runFailure is not null
                => string.IsNullOrEmpty(runFailure.ReasonCode)
                    ? RunTerminalReasonCodes.Failed
                    : runFailure.ReasonCode,
            RunTerminalStatus.Cancelled => RunTerminalReasonCodes.Cancelled,
            RunTerminalStatus.NeedsManualReview => RunTerminalReasonCodes.NeedsManualReview,
            RunTerminalStatus.PartialSuccess => RunTerminalReasonCodes.PartialSuccess,
            _ => RunTerminalReasonCodes.Completed,
        };

        var summary = new RunSummary(
            RunId: runId,
            TerminalStatus: status,
            TerminalReasonCode: reasonCode,
            ResolvedCount: counts.GetValueOrDefault(CandidateStatus.Resolved, 0),
            DuplicateCount: counts.GetValueOrDefault(CandidateStatus.Duplicate, 0),
            RetainedCount: counts.GetValueOrDefault(CandidateStatus.Retained, 0),
            ManualReviewCount: counts.GetValueOrDefault(CandidateStatus.ManualReview, 0),
            UnresolvedCount: counts.GetValueOrDefault(CandidateStatus.Unresolved, 0),
            CancelledCount: counts.GetValueOrDefault(CandidateStatus.Cancelled, 0),
            QuotaExhaustedCount: counts.GetValueOrDefault(CandidateStatus.QuotaExhausted, 0),
            AuthFailedCount: counts.GetValueOrDefault(CandidateStatus.AuthFailed, 0),
            TimeoutCount: counts.GetValueOrDefault(CandidateStatus.Timeout, 0),
            FailureReasonCounts: reasonCounts,
            ReportPath: null,
            ReportContentHash: null,
            CompletedAtUtc: completedAtUtc,
            ReportExportQueued: false,
            FinalizerFailures: finalizerFailures);

        return new RunTerminalDecision(runId, status, reasonCode, FinalBarrierReached: true, summary);
    }

    private static RunTerminalDecision BuildDeferred(
        string runId,
        IReadOnlyDictionary<CandidateStatus, int> counts,
        IReadOnlyDictionary<string, int> reasonCounts,
        IReadOnlyList<RunFailure> finalizerFailures,
        DateTimeOffset completedAtUtc)
    {
        var summary = new RunSummary(
            RunId: runId,
            TerminalStatus: RunTerminalStatus.Completed,
            TerminalReasonCode: RunTerminalReasonCodes.Completed,
            ResolvedCount: counts.GetValueOrDefault(CandidateStatus.Resolved, 0),
            DuplicateCount: counts.GetValueOrDefault(CandidateStatus.Duplicate, 0),
            RetainedCount: counts.GetValueOrDefault(CandidateStatus.Retained, 0),
            ManualReviewCount: counts.GetValueOrDefault(CandidateStatus.ManualReview, 0),
            UnresolvedCount: counts.GetValueOrDefault(CandidateStatus.Unresolved, 0),
            CancelledCount: counts.GetValueOrDefault(CandidateStatus.Cancelled, 0),
            QuotaExhaustedCount: counts.GetValueOrDefault(CandidateStatus.QuotaExhausted, 0),
            AuthFailedCount: counts.GetValueOrDefault(CandidateStatus.AuthFailed, 0),
            TimeoutCount: counts.GetValueOrDefault(CandidateStatus.Timeout, 0),
            FailureReasonCounts: reasonCounts,
            ReportPath: null,
            ReportContentHash: null,
            CompletedAtUtc: completedAtUtc,
            ReportExportQueued: false,
            FinalizerFailures: finalizerFailures);
        return new RunTerminalDecision(runId, RunTerminalStatus.Completed, RunTerminalReasonCodes.Completed, FinalBarrierReached: false, summary);
    }

    private static IReadOnlyDictionary<CandidateStatus, int> CountCandidates(
        IReadOnlyList<CandidateProcessResult> candidates)
    {
        var counts = new Dictionary<CandidateStatus, int>();
        foreach (var c in candidates)
        {
            counts[c.Status] = counts.GetValueOrDefault(c.Status, 0) + 1;
        }
        return counts;
    }

    private static IReadOnlyDictionary<string, int> CountReasons(
        IReadOnlyList<CandidateProcessResult> candidates)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var reason = c.Failure?.ReasonCode;
            if (string.IsNullOrEmpty(reason)) continue;
            counts[reason] = counts.GetValueOrDefault(reason, 0) + 1;
        }
        return counts;
    }
}