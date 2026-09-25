// Verifies RunCoordinator from design §5:
//   * CommitPacketAsync writes event + audit + checkpoint + cursor in one UoW
//   * Duplicate CommitPacketAsync on a terminal run returns the existing
//     decision without throwing or writing
//   * FinalizeAsync persists terminal state and audit + run-event in one UoW
//   * FinalizeAsync on an already-terminal run returns the prior decision
//     without writing again
//   * FinalizeAsync without FinalBarrierReached throws

using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class RunCoordinatorTests
{
    [Fact]
    public async Task CommitPacketAsync_writes_event_audit_checkpoint_cursor_in_one_uow()
    {
        var fakes = new Fakes();
        var coordinator = new RunCoordinator(
            fakes.UowFactory,
            fakes.LifecycleStore,
            fakes.CheckpointStore,
            fakes.EventStore,
            fakes.AuditStore,
            new TerminalDecisionService());

        var result = await coordinator.CommitPacketAsync(
            new PacketCommitRequest(
                RunId: "run-1",
                NodeId: "scan-mailbox",
                Stage: "scan-mailbox",
                EventSequence: 1,
                EventType: "run.started",
                EventPayloadJson: "{\"hello\":1}",
                EmittedAtUtc: DateTimeOffset.UtcNow,
                EventExpiresAtUtc: null,
                CandidateResults: new[]
                {
                    NewCandidateResult(CandidateStatus.Resolved),
                    NewCandidateResult(CandidateStatus.Resolved),
                }),
            CancellationToken.None);

        result.RunTerminal.Should().BeFalse();
        result.NextSequence.Should().Be(2);
        result.TerminalDecision.Should().BeNull();

        fakes.EventStore.Count.Should().Be(1);
        fakes.AuditStore.Count.Should().Be(1);
        fakes.CheckpointStore.LastWritten.Should().NotBeNull();
        fakes.CheckpointStore.LastWritten!.LastCommittedSequence.Should().Be(1);
        fakes.LifecycleStore.LastSequenceWritten.Should().Be(1);
        fakes.UowFactory.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task CommitPacketAsync_on_terminal_run_returns_existing_decision()
    {
        var fakes = new Fakes();
        var coordinator = new RunCoordinator(
            fakes.UowFactory,
            fakes.LifecycleStore,
            fakes.CheckpointStore,
            fakes.EventStore,
            fakes.AuditStore,
            new TerminalDecisionService());

        // Mark the run as already-terminal via the fake lifecycle store.
        fakes.LifecycleStore.SetState("run-1", new RunStateSnapshot(
            "run-1",
            RunLifecycleState.Completed,
            "lifecycle",
            TerminalReasonCode: RunTerminalReasonCodes.Completed,
            LastEventSequence: 5,
            EndedAtUtc: DateTimeOffset.UtcNow,
            CancellationRequestedAtUtc: null));

        var result = await coordinator.CommitPacketAsync(
            new PacketCommitRequest(
                "run-1", "scan-mailbox", "scan-mailbox", 6, "extra",
                "{}", DateTimeOffset.UtcNow, null, Array.Empty<CandidateProcessResult>()),
            CancellationToken.None);

        result.RunTerminal.Should().BeTrue();
        result.TerminalDecision.Should().NotBeNull();
        result.TerminalDecision!.Status.Should().Be(RunTerminalStatus.Completed);
        fakes.EventStore.Count.Should().Be(0); // No writes on already-terminal
        fakes.UowFactory.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task FinalizeAsync_persists_terminal_state_and_audit_in_one_uow()
    {
        var fakes = new Fakes();
        fakes.LifecycleStore.SetState("run-1", new RunStateSnapshot(
            "run-1", RunLifecycleState.Running, "scan-mailbox", null, 3, null, null));

        var coordinator = new RunCoordinator(
            fakes.UowFactory,
            fakes.LifecycleStore,
            fakes.CheckpointStore,
            fakes.EventStore,
            fakes.AuditStore,
            new TerminalDecisionService());

        var decision = await coordinator.FinalizeAsync(
            new RunFinalizationRequest(
                RunId: "run-1",
                CancellationRequested: false,
                AllCandidatesArrived: true,
                CandidateResults: new[]
                {
                    NewCandidateResult(CandidateStatus.Resolved),
                    NewCandidateResult(CandidateStatus.Resolved),
                },
                RunFailure: null,
                FinalizerFailures: Array.Empty<RunFailure>(),
                CompletedAtUtc: DateTimeOffset.UtcNow,
                ReportPath: "reports/run-1/report.xlsx",
                ReportContentHash: "report-hash"),
            CancellationToken.None);

        decision.Status.Should().Be(RunTerminalStatus.Completed);
        decision.FinalBarrierReached.Should().BeTrue();
        decision.ReasonCode.Should().Be(RunTerminalReasonCodes.Completed);
        decision.TerminalEventSequence.Should().Be(4);

        fakes.EventStore.Count.Should().Be(1); // run.terminal event appended
        fakes.EventStore.LastType.Should().Be("run.terminal");
        fakes.AuditStore.Count.Should().Be(1);
        fakes.LifecycleStore.LastTerminalStateWritten.Should().Be(RunLifecycleState.Completed);
        fakes.LifecycleStore.LastTerminalSummary!.ReportPath.Should().Be("reports/run-1/report.xlsx");
        fakes.LifecycleStore.LastTerminalSummary.ReportContentHash.Should().Be("report-hash");
        fakes.UowFactory.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task FinalizeAsync_is_idempotent_for_already_terminal_run()
    {
        var fakes = new Fakes();
        fakes.LifecycleStore.SetState("run-1", new RunStateSnapshot(
            "run-1", RunLifecycleState.Completed, "lifecycle",
            RunTerminalReasonCodes.Completed, 5, DateTimeOffset.UtcNow, null));

        var coordinator = new RunCoordinator(
            fakes.UowFactory,
            fakes.LifecycleStore,
            fakes.CheckpointStore,
            fakes.EventStore,
            fakes.AuditStore,
            new TerminalDecisionService());

        var decision = await coordinator.FinalizeAsync(
            new RunFinalizationRequest(
                "run-1", false, true,
                Array.Empty<CandidateProcessResult>(),
                null,
                Array.Empty<RunFailure>(),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        decision.Status.Should().Be(RunTerminalStatus.Completed);
        decision.FinalBarrierReached.Should().BeTrue();
        decision.TerminalEventSequence.Should().Be(5);
        fakes.EventStore.Count.Should().Be(0);
        fakes.UowFactory.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task FinalizeAsync_throws_when_barrier_not_reached()
    {
        var fakes = new Fakes();
        fakes.LifecycleStore.SetState("run-1", new RunStateSnapshot(
            "run-1", RunLifecycleState.Running, "scan-mailbox", null, 0, null, null));

        var coordinator = new RunCoordinator(
            fakes.UowFactory,
            fakes.LifecycleStore,
            fakes.CheckpointStore,
            fakes.EventStore,
            fakes.AuditStore,
            new TerminalDecisionService());

        var act = async () => await coordinator.FinalizeAsync(
            new RunFinalizationRequest(
                "run-1", false, false,
                Array.Empty<CandidateProcessResult>(),
                null,
                Array.Empty<RunFailure>(),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        fakes.UowFactory.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task FinalizeAsync_run_failure_dominates_partial_success()
    {
        var fakes = new Fakes();
        fakes.LifecycleStore.SetState("run-1", new RunStateSnapshot(
            "run-1", RunLifecycleState.Running, "scan-mailbox", null, 1, null, null));

        var coordinator = new RunCoordinator(
            fakes.UowFactory,
            fakes.LifecycleStore,
            fakes.CheckpointStore,
            fakes.EventStore,
            fakes.AuditStore,
            new TerminalDecisionService());

        var decision = await coordinator.FinalizeAsync(
            new RunFinalizationRequest(
                RunId: "run-1",
                CancellationRequested: false,
                AllCandidatesArrived: true,
                CandidateResults: new[]
                {
                    NewCandidateResult(CandidateStatus.Resolved),
                    NewCandidateResult(CandidateStatus.Unresolved),
                },
                RunFailure: new RunFailure("run-1", "lifecycle", "DB_WRITE_FAILED",
                    FailureCategory.Persistence, Retryable: false, SafeMessage: "persistence dropped"),
                FinalizerFailures: Array.Empty<RunFailure>(),
                CompletedAtUtc: DateTimeOffset.UtcNow),
            CancellationToken.None);

        decision.Status.Should().Be(RunTerminalStatus.Failed);
        decision.ReasonCode.Should().Be("DB_WRITE_FAILED");
    }

    private static CandidateProcessResult NewCandidateResult(CandidateStatus status) =>
        new(NewCandidate(), status);

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

    private sealed class Fakes
    {
        public FakeUowFactory UowFactory { get; } = new();
        public FakeLifecycleStore LifecycleStore { get; } = new();
        public FakeCheckpointStore CheckpointStore { get; } = new();
        public FakeEventStore EventStore { get; } = new();
        public FakeAuditStore AuditStore { get; } = new();
    }

    private sealed class FakeUowFactory : IUnitOfWorkFactory
    {
        public int CommitCount { get; private set; }
        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
            => Task.FromResult<IUnitOfWork>(new FakeUow(this));

        private sealed class FakeUow : IUnitOfWork
        {
            private readonly FakeUowFactory _factory;
            public FakeUow(FakeUowFactory factory)
            {
                _factory = factory;
                TransactionId = Guid.NewGuid().ToString("N");
                Purpose = TransactionPurpose.PacketCommit;
            }
            public string TransactionId { get; }
            public TransactionPurpose Purpose { get; }
            public bool IsCompleted { get; private set; }
            public Task CommitAsync(CancellationToken cancellationToken) { IsCompleted = true; _factory.CommitCount++; return Task.CompletedTask; }
            public Task RollbackAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLifecycleStore : IRunLifecycleStore
    {
        private readonly Dictionary<string, RunStateSnapshot> _states = new();
        public long? LastSequenceWritten { get; private set; }
        public RunLifecycleState? LastTerminalStateWritten { get; private set; }
    public RunSummary? LastTerminalSummary { get; private set; }

        public void SetState(string runId, RunStateSnapshot snapshot) => _states[runId] = snapshot;

        public Task<RunStateSnapshot?> FindAsync(string runId, CancellationToken cancellationToken)
        {
            _states.TryGetValue(runId, out var s);
            return Task.FromResult<RunStateSnapshot?>(s);
        }

        public Task<bool> TryCreateAsync(RunCreationRequest request, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            if (_states.Values.Any(state => state.State is RunLifecycleState.Created or RunLifecycleState.Running or RunLifecycleState.Recovering))
                return Task.FromResult(false);
            _states[request.RunId] = new RunStateSnapshot(request.RunId, RunLifecycleState.Created, "admitted", null, 0, null, null);
            return Task.FromResult(true);
        }

        public Task<bool> TryMarkRunningAsync(string runId, string stage, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            if (!_states.TryGetValue(runId, out var state) || state.State != RunLifecycleState.Created)
                return Task.FromResult(false);
            _states[runId] = state with { State = RunLifecycleState.Running, Stage = stage };
            return Task.FromResult(true);
        }

        public Task UpdateTerminalStateAsync(RunStateSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            _states[snapshot.RunId] = snapshot;
            LastTerminalStateWritten = snapshot.State;
            LastTerminalSummary = snapshot.Summary;
            return Task.CompletedTask;
        }

        public Task UpdateLastEventSequenceAsync(string runId, long lastEventSequence, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            LastSequenceWritten = lastEventSequence;
            if (_states.TryGetValue(runId, out var existing))
            {
                _states[runId] = existing with { LastEventSequence = Math.Max(existing.LastEventSequence, lastEventSequence) };
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryRequestCancellationAsync(string runId, DateTimeOffset requestedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            if (!_states.TryGetValue(runId, out var existing)
                || existing.State is not (RunLifecycleState.Created or RunLifecycleState.Running or RunLifecycleState.Recovering)
                || existing.CancellationRequestedAtUtc is not null)
                return Task.FromResult(false);
            _states[runId] = existing with { CancellationRequestedAtUtc = requestedAtUtc };
            return Task.FromResult(true);
        }

        public Task<bool> IsCancellationRequestedAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult(_states.TryGetValue(runId, out var s) && s.CancellationRequestedAtUtc is not null);
    }

    private sealed class FakeCheckpointStore : IRunCheckpointStore
    {
        private readonly Dictionary<(string, string), RunCheckpointSnapshot> _store = new();
        public RunCheckpointSnapshot? LastWritten { get; private set; }

        public Task<RunCheckpointSnapshot?> ReadAsync(string runId, string nodeId, CancellationToken cancellationToken)
        {
            _store.TryGetValue((runId, nodeId), out var snapshot);
            return Task.FromResult<RunCheckpointSnapshot?>(snapshot);
        }

        public Task WriteAsync(RunCheckpointSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            _store[(snapshot.RunId, snapshot.NodeId)] = snapshot;
            LastWritten = snapshot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpointSnapshot>> ListAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RunCheckpointSnapshot>>(_store.Where(kv => kv.Key.Item1 == runId).Select(kv => kv.Value).ToList());
    }

    private sealed class FakeEventStore : IEventReplayStore
    {
        private readonly List<StoredRunEventRecord> _events = new();
        public int Count => _events.Count;
        public string? LastType => _events.Count == 0 ? null : _events[^1].EventType;

        public Task AppendAsync(StoredRunEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            _events.Add(record);
            return Task.CompletedTask;
        }

        public Task<EventReplayResultRecord> ReadSinceAsync(string runId, long afterSequence, int limit, CancellationToken cancellationToken)
        {
            var page = _events
                .Where(e => e.RunId == runId && e.EventSequence > afterSequence)
                .OrderBy(e => e.EventSequence)
                .Take(limit)
                .ToList();
            var latest = page.Count == 0 ? afterSequence : page[^1].EventSequence;
            return Task.FromResult(new EventReplayResultRecord(page, latest, false));
        }
    }

    private sealed class FakeAuditStore : IAuditEventStore
    {
        public int Count { get; private set; }
        public Task AppendAsync(AuditEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}