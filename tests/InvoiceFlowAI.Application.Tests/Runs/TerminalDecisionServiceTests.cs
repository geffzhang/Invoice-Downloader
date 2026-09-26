// Verifies the terminal decision priority and barrier semantics from
// design §5:
//   Failed > Cancelled > NeedsManualReview > PartialSuccess > Completed
// plus the auxiliary rules:
//   * Candidate failures stay attached to CandidateProcessResult values
//   * Critical finalizer failures elevate Completed to Failed
//   * Cancellation is only honored after all candidate outcomes arrived
//   * A no-candidate completed call is FinalBarrierReached=true so the
//     coordinator can persist the run as Completed.

using FluentAssertions;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class TerminalDecisionServiceTests
{
    private readonly TerminalDecisionService _service = new();

    [Fact]
    public void All_candidates_resolved_returns_completed()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(CandidateStatus.Resolved, CandidateStatus.Resolved, CandidateStatus.Duplicate),
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.Completed);
        decision.ReasonCode.Should().Be(RunTerminalReasonCodes.Completed);
        decision.FinalBarrierReached.Should().BeTrue();
        decision.Summary.ResolvedCount.Should().Be(2);
        decision.Summary.DuplicateCount.Should().Be(1);
        decision.Summary.UnresolvedCount.Should().Be(0);
    }

    [Fact]
    public void Manual_review_candidate_escalates_to_needs_manual_review()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(CandidateStatus.Resolved, CandidateStatus.ManualReview),
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.NeedsManualReview);
        decision.ReasonCode.Should().Be(RunTerminalReasonCodes.NeedsManualReview);
        decision.Summary.ManualReviewCount.Should().Be(1);
    }

    [Fact]
    public void Unresolved_or_timeout_or_quota_exhausted_yields_partial_success()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(
                CandidateStatus.Resolved,
                CandidateStatus.Unresolved,
                CandidateStatus.Timeout,
                CandidateStatus.QuotaExhausted),
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.PartialSuccess);
        decision.ReasonCode.Should().Be(RunTerminalReasonCodes.PartialSuccess);
        decision.Summary.UnresolvedCount.Should().Be(1);
        decision.Summary.TimeoutCount.Should().Be(1);
        decision.Summary.QuotaExhaustedCount.Should().Be(1);
    }

    [Fact]
    public void Run_failure_takes_priority_over_partial_success()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(CandidateStatus.Resolved, CandidateStatus.Unresolved),
            runFailure: NewRunFailure("DB_WRITE_FAILED"),
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.Failed);
        decision.ReasonCode.Should().Be("DB_WRITE_FAILED");
    }

    [Fact]
    public void Cancellation_with_all_candidates_arrived_is_cancelled()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(CandidateStatus.Resolved, CandidateStatus.Cancelled),
            runFailure: null,
            cancellationRequested: true,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.Cancelled);
        decision.ReasonCode.Should().Be(RunTerminalReasonCodes.Cancelled);
        decision.Summary.CancelledCount.Should().Be(1);
    }

    [Fact]
    public void Cancellation_without_all_candidates_arrived_keeps_running_failed()
    {
        // Per design, cancellation is only honored at the final barrier —
        // until then the run is treated as Failed if no decision can be
        // produced yet, otherwise it stays Partial / Completed depending
        // on the candidate mix.
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(CandidateStatus.Resolved),
            runFailure: null,
            cancellationRequested: true,
            allCandidatesArrived: false,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.Completed);
        decision.FinalBarrierReached.Should().BeFalse();
    }

    [Fact]
    public void Critical_finalizer_failure_elevates_completed_to_failed()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(CandidateStatus.Resolved, CandidateStatus.Resolved),
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: new[]
            {
                new RunFailure("run-1", "lifecycle", "PERSISTENCE_DROPPED",
                    FailureCategory.Persistence, Retryable: false, SafeMessage: "audit write failed"),
            },
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.Failed);
        decision.ReasonCode.Should().Be(RunTerminalReasonCodes.FinalizerFailed);
    }

    [Fact]
    public void Auth_failed_candidate_is_a_partial_success()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Candidates(CandidateStatus.Resolved, CandidateStatus.AuthFailed),
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.PartialSuccess);
        decision.Summary.AuthFailedCount.Should().Be(1);
    }

    [Fact]
    public void No_candidates_with_barrier_signal_returns_completed()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: Array.Empty<CandidateProcessResult>(),
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.Completed);
        decision.FinalBarrierReached.Should().BeTrue();
    }

    [Fact]
    public void Reason_counts_are_aggregated_by_code()
    {
        var decision = _service.Decide(
            "run-1",
            candidates: new[]
            {
                CandidateWithFailure(CandidateStatus.Unresolved, "OCR_TIMEOUT"),
                CandidateWithFailure(CandidateStatus.Unresolved, "OCR_TIMEOUT"),
                CandidateWithFailure(CandidateStatus.QuotaExhausted, "QUOTA_DEEPSEEK"),
            },
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            finalizerFailures: Array.Empty<RunFailure>(),
            completedAtUtc: DateTimeOffset.UtcNow);

        decision.Status.Should().Be(RunTerminalStatus.PartialSuccess);
        decision.Summary.FailureReasonCounts["OCR_TIMEOUT"].Should().Be(2);
        decision.Summary.FailureReasonCounts["QUOTA_DEEPSEEK"].Should().Be(1);
    }

    private static CandidateProcessResult[] Candidates(params CandidateStatus[] kinds) =>
        kinds.Select(k => new CandidateProcessResult(
            Candidate: NewCandidate(),
            Status: k)).ToArray();

    private static CandidateProcessResult CandidateWithFailure(CandidateStatus status, string reasonCode) =>
        new(NewCandidate(), status, Failure: new CandidateFailure(
            ReasonCode: reasonCode,
            Scope: FailureScope.Candidate,
            Category: FailureCategory.Input,
            Retryable: false,
            SafeMessage: "candidate failure"));

    private static DocumentCandidate NewCandidate() => new(
        DocumentId: DocumentIdentity.Create(Guid.NewGuid().ToString("N")),
        Sequence: 1,
        CorrelationId: Guid.NewGuid().ToString("N"),
        SourceMessageUid: "1",
        OriginalFileName: "invoice.pdf",
        ContentType: "application/pdf",
        ContentLength: 1024,
        ProcessingRevision: 1,
        SourceKind: "email");

    private static RunFailure NewRunFailure(string reasonCode) =>
        new("run-1", "lifecycle", reasonCode, FailureCategory.Persistence,
            Retryable: false, SafeMessage: "persistence dropped");
}