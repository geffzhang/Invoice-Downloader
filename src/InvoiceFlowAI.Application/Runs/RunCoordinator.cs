// Run coordinator: drives a single run from "started" to terminal state.
// Each committed packet is wrapped in one UoW so the business state write,
// the audit event, the run-event, the checkpoint, and the
// Runs.LastEventSequence advance all commit together or not at all (design
// §5). The terminal decision is computed exactly once at the final barrier
// and re-applied to any subsequent "complete" request — the coordinator
// is idempotent for already-terminal runs.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Runs;

public interface IRunCoordinator
{
    Task<RunCommitResult> CommitPacketAsync(
        PacketCommitRequest request,
        CancellationToken cancellationToken);

    Task<RunTerminalDecision> FinalizeAsync(
        RunFinalizationRequest request,
        CancellationToken cancellationToken);
}

public sealed record PacketCommitRequest(
    string RunId,
    string NodeId,
    string Stage,
    long EventSequence,
    string EventType,
    string EventPayloadJson,
    DateTimeOffset EmittedAtUtc,
    DateTimeOffset? EventExpiresAtUtc,
    IReadOnlyList<CandidateProcessResult> CandidateResults);

public sealed record RunCommitResult(
    string RunId,
    long LastEventSequence,
    long NextSequence,
    bool RunTerminal,
    RunTerminalDecision? TerminalDecision);

public sealed record RunFinalizationRequest(
    string RunId,
    bool CancellationRequested,
    bool AllCandidatesArrived,
    IReadOnlyList<CandidateProcessResult> CandidateResults,
    RunFailure? RunFailure,
    IReadOnlyList<RunFailure> FinalizerFailures,
    DateTimeOffset CompletedAtUtc,
    string? ReportPath = null,
    string? ReportContentHash = null);

public sealed class RunCoordinator : IRunCoordinator
{
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly IRunLifecycleStore _lifecycleStore;
    private readonly IRunCheckpointStore _checkpointStore;
    private readonly IEventReplayStore _eventStore;
    private readonly IAuditEventStore _auditStore;
    private readonly ITerminalDecisionService _decisionService;

    public RunCoordinator(
        IUnitOfWorkFactory uowFactory,
        IRunLifecycleStore lifecycleStore,
        IRunCheckpointStore checkpointStore,
        IEventReplayStore eventStore,
        IAuditEventStore auditStore,
        ITerminalDecisionService decisionService)
    {
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _lifecycleStore = lifecycleStore ?? throw new ArgumentNullException(nameof(lifecycleStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
    }

    public async Task<RunCommitResult> CommitPacketAsync(
        PacketCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.RunId);

        // Idempotency: if the run is already terminal, return the existing
        // decision rather than accepting new packets. This protects against
        // duplicate "completed" callbacks reaching the database.
        var existing = await _lifecycleStore.FindAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && IsTerminalState(existing.State))
        {
            // Rebuild a decision from a thread-local snapshot so the caller can
            // still see terminal state without round-tripping to the DB.
            var decision = RebuildTerminalDecision(existing, request.CandidateResults);
            return new RunCommitResult(
                request.RunId,
                existing.LastEventSequence,
                NextSequence: existing.LastEventSequence + 1,
                RunTerminal: true,
                TerminalDecision: decision);
        }

        await using var uow = await _uowFactory.BeginAsync(TransactionPurpose.PacketCommit, cancellationToken).ConfigureAwait(false);

        // Append the run-event first; the (RunId, EventSequence) unique
        // index acts as the atomic gate — a duplicate replays here will
        // raise a unique-constraint violation that the caller maps to
        // idempotent no-op.
        var storedEvent = new StoredRunEventRecord(
            RunId: request.RunId,
            EventSequence: request.EventSequence,
            EventType: request.EventType,
            PayloadJson: request.EventPayloadJson,
            EmittedAtUtc: request.EmittedAtUtc,
            ExpiresAtUtc: request.EventExpiresAtUtc);
        await _eventStore.AppendAsync(storedEvent, uow, cancellationToken).ConfigureAwait(false);

        // Audit the packet so the run has a tamper-evident log even when the
        // run-event retention window expires.
        var audit = new AuditEventRecord(
            AuditEventId: $"audit-{request.RunId}-{request.EventSequence:D20}",
            RunId: request.RunId,
            EventSequence: request.EventSequence,
            EventType: request.EventType,
            Stage: request.Stage,
            NodeId: request.NodeId,
            DocumentId: null,
            ProcessingRevision: null,
            ReasonCode: "",
            PayloadJson: request.EventPayloadJson,
            PayloadHash: Sha256Hex(request.EventPayloadJson),
            OccurredAtUtc: request.EmittedAtUtc);
        await _auditStore.AppendAsync(audit, uow, cancellationToken).ConfigureAwait(false);

        // Advance the per-run sequence cursor.
        await _lifecycleStore.UpdateLastEventSequenceAsync(
            request.RunId,
            request.EventSequence,
            uow,
            cancellationToken).ConfigureAwait(false);

        // Write the per-node checkpoint that pairs with the event sequence.
        var checkpoint = new RunCheckpointSnapshot(
            RunId: request.RunId,
            NodeId: request.NodeId,
            Stage: request.Stage,
            LastCommittedSequence: request.EventSequence,
            OutputCount: request.CandidateResults.Count,
            State: "Active",
            CheckpointRevision: 1, // incremented in CheckpointService on subsequent calls
            UpdatedAtUtc: request.EmittedAtUtc);
        await _checkpointStore.WriteAsync(checkpoint, uow, cancellationToken).ConfigureAwait(false);

        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RunCommitResult(
            request.RunId,
            request.EventSequence,
            NextSequence: request.EventSequence + 1,
            RunTerminal: false,
            TerminalDecision: null);
    }

    public async Task<RunTerminalDecision> FinalizeAsync(
        RunFinalizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await _lifecycleStore.FindAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && IsTerminalState(existing.State))
        {
            return RebuildTerminalDecision(existing, request.CandidateResults);
        }

        var decision = _decisionService.Decide(
            request.RunId,
            request.CandidateResults,
            request.RunFailure,
            request.CancellationRequested,
            request.AllCandidatesArrived,
            request.FinalizerFailures,
            request.CompletedAtUtc);

        if (!decision.FinalBarrierReached)
        {
            throw new InvalidOperationException(
                $"Finalization called before barrier reached for run '{request.RunId}'.");
        }

        if ((request.ReportPath is null) != (request.ReportContentHash is null))
        {
            throw new ArgumentException("Report path and content hash must be supplied together.", nameof(request));
        }
        if (request.ReportPath is not null)
        {
            decision = decision with
            {
                Summary = decision.Summary with
                {
                    ReportPath = request.ReportPath,
                    ReportContentHash = request.ReportContentHash,
                },
            };
        }

        await using var uow = await _uowFactory.BeginAsync(TransactionPurpose.TerminalCommit, cancellationToken).ConfigureAwait(false);

        // Audit the terminal transition (an extra event appended *after*
        // the last committed packet's sequence).
        var terminalSequence = (existing?.LastEventSequence ?? 0) + 1;
        var payloadJson = $"{{\"status\":\"{decision.Status}\",\"reason\":\"{decision.ReasonCode}\"}}";
        var terminalEvent = new StoredRunEventRecord(
            request.RunId,
            terminalSequence,
            EventType: "run.terminal",
            PayloadJson: payloadJson,
            EmittedAtUtc: request.CompletedAtUtc,
            ExpiresAtUtc: null);
        await _eventStore.AppendAsync(terminalEvent, uow, cancellationToken).ConfigureAwait(false);

        var terminalAudit = new AuditEventRecord(
            AuditEventId: $"audit-{request.RunId}-{terminalSequence:D20}",
            RunId: request.RunId,
            EventSequence: terminalSequence,
            EventType: "run.terminal",
            Stage: "lifecycle",
            NodeId: "lifecycle",
            DocumentId: null,
            ProcessingRevision: null,
            ReasonCode: decision.ReasonCode,
            PayloadJson: payloadJson,
            PayloadHash: Sha256Hex(payloadJson),
            OccurredAtUtc: request.CompletedAtUtc);
        await _auditStore.AppendAsync(terminalAudit, uow, cancellationToken).ConfigureAwait(false);

        var newState = MapStatus(decision.Status);
        var snapshot = new RunStateSnapshot(
            RunId: request.RunId,
            State: newState,
            Stage: "lifecycle",
            TerminalReasonCode: decision.ReasonCode,
            LastEventSequence: terminalSequence,
            EndedAtUtc: request.CompletedAtUtc,
            CancellationRequestedAtUtc: existing?.CancellationRequestedAtUtc,
            Summary: decision.Summary);
        await _lifecycleStore.UpdateTerminalStateAsync(snapshot, uow, cancellationToken).ConfigureAwait(false);

        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);

        return decision with { TerminalEventSequence = terminalSequence };
    }

    private static bool IsTerminalState(RunLifecycleState state) =>
        state is RunLifecycleState.Completed
                or RunLifecycleState.PartialSuccess
                or RunLifecycleState.NeedsManualReview
                or RunLifecycleState.Cancelled
                or RunLifecycleState.Failed;

    private static RunLifecycleState MapStatus(RunTerminalStatus status) => status switch
    {
        RunTerminalStatus.Completed => RunLifecycleState.Completed,
        RunTerminalStatus.PartialSuccess => RunLifecycleState.PartialSuccess,
        RunTerminalStatus.NeedsManualReview => RunLifecycleState.NeedsManualReview,
        RunTerminalStatus.Cancelled => RunLifecycleState.Cancelled,
        RunTerminalStatus.Failed => RunLifecycleState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static RunTerminalDecision RebuildTerminalDecision(
        RunStateSnapshot snapshot,
        IReadOnlyList<CandidateProcessResult> candidates)
    {
        if (snapshot.Summary is not null)
        {
            return new RunTerminalDecision(
                snapshot.RunId,
                snapshot.Summary.TerminalStatus,
                snapshot.Summary.TerminalReasonCode,
                true,
                snapshot.Summary,
                snapshot.LastEventSequence);
        }

        var counts = new Dictionary<CandidateStatus, int>();
        foreach (var c in candidates)
        {
            counts[c.Status] = counts.GetValueOrDefault(c.Status, 0) + 1;
        }
        var status = MapStatusReverse(snapshot.State);
        var summary = new RunSummary(
            RunId: snapshot.RunId,
            TerminalStatus: status,
            TerminalReasonCode: snapshot.TerminalReasonCode ?? "",
            ResolvedCount: counts.GetValueOrDefault(CandidateStatus.Resolved, 0),
            DuplicateCount: counts.GetValueOrDefault(CandidateStatus.Duplicate, 0),
            RetainedCount: counts.GetValueOrDefault(CandidateStatus.Retained, 0),
            ManualReviewCount: counts.GetValueOrDefault(CandidateStatus.ManualReview, 0),
            UnresolvedCount: counts.GetValueOrDefault(CandidateStatus.Unresolved, 0),
            CancelledCount: counts.GetValueOrDefault(CandidateStatus.Cancelled, 0),
            QuotaExhaustedCount: counts.GetValueOrDefault(CandidateStatus.QuotaExhausted, 0),
            AuthFailedCount: counts.GetValueOrDefault(CandidateStatus.AuthFailed, 0),
            TimeoutCount: counts.GetValueOrDefault(CandidateStatus.Timeout, 0),
            FailureReasonCounts: new Dictionary<string, int>(StringComparer.Ordinal),
            ReportPath: null,
            ReportContentHash: null,
            CompletedAtUtc: snapshot.EndedAtUtc ?? DateTimeOffset.UtcNow,
            ReportExportQueued: false,
            FinalizerFailures: Array.Empty<RunFailure>());
        return new RunTerminalDecision(
            snapshot.RunId,
            status,
            snapshot.TerminalReasonCode ?? "",
            true,
            summary,
            snapshot.LastEventSequence);
    }

    private static RunTerminalStatus MapStatusReverse(RunLifecycleState state) => state switch
    {
        RunLifecycleState.Completed => RunTerminalStatus.Completed,
        RunLifecycleState.PartialSuccess => RunTerminalStatus.PartialSuccess,
        RunLifecycleState.NeedsManualReview => RunTerminalStatus.NeedsManualReview,
        RunLifecycleState.Cancelled => RunTerminalStatus.Cancelled,
        RunLifecycleState.Failed => RunTerminalStatus.Failed,
        _ => RunTerminalStatus.Failed,
    };

    private static string Sha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}