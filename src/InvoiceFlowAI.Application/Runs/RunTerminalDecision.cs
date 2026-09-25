// Domain payload produced exactly once at the final barrier. The decision
// is the *only* outcome the run may persist; subsequent calls return the
// existing record so callers see idempotency (design §11).

using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Runs;

/// <summary>
/// Result of the post-barrier terminal decision. Includes the new run state,
/// reason code, summary, and a flag indicating whether the final barrier was
/// reached. Stored once in Runs.SummaryJson / TerminalReasonCode / State.
/// </summary>
public sealed record RunTerminalDecision(
    string RunId,
    RunTerminalStatus Status,
    string ReasonCode,
    bool FinalBarrierReached,
    RunSummary Summary,
    long TerminalEventSequence = 0);

/// <summary>Stable reason codes attached to terminal decisions.</summary>
public static class RunTerminalReasonCodes
{
    public const string Completed = "RUN_COMPLETED";
    public const string PartialSuccess = "RUN_PARTIAL_SUCCESS";
    public const string NeedsManualReview = "RUN_NEEDS_MANUAL_REVIEW";
    public const string Cancelled = "RUN_CANCELLED";
    public const string Failed = "RUN_FAILED";
    public const string FinalizerFailed = "RUN_FINALIZER_FAILED";
}