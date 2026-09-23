// Application service that computes the final terminal decision for a run.
// Per design §5 the priority is:
//   Failed > Cancelled > NeedsManualReview > PartialSuccess > Completed
// and the decision is computed *exactly once* at the final barrier. Pure
// logic — no I/O — so the service can be exercised in unit tests without a
// real database.

using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Runs;

public interface ITerminalDecisionService
{
    RunTerminalDecision Decide(
        string runId,
        IReadOnlyList<CandidateProcessResult> candidates,
        RunFailure? runFailure,
        bool cancellationRequested,
        bool allCandidatesArrived,
        IReadOnlyList<RunFailure> finalizerFailures,
        DateTimeOffset completedAtUtc);
}