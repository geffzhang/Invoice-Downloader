using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class DesktopRunServiceTests
{
    [Fact]
    public async Task Start_reserves_and_launches_one_run_then_rejects_a_second()
    {
        var fixture = new ServiceFixture();
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Execute = async (_, _) => await releaseExecution.Task;

        var first = await fixture.Service.StartAsync(NewRequest("run-1"), CancellationToken.None);
        await fixture.Executor.Entered.Task;
        var second = await fixture.Service.StartAsync(NewRequest("run-2"), CancellationToken.None);

        first.Accepted.Should().BeTrue();
        first.RunId.Should().Be("run-1");
        second.Accepted.Should().BeFalse();
        second.RejectionCode.Should().Be(RpcErrorCodes.RunAlreadyActive);
        fixture.Lifecycle.TryCreateCalls.Should().Be(1);

        releaseExecution.SetResult();
        await fixture.Registry.WaitForIdleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Background_execution_disposes_its_executor_lease_after_completion()
    {
        var fixture = new ServiceFixture();
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Execute = async (_, _) => await releaseExecution.Task;

        var result = await fixture.Service.StartAsync(NewRequest("run-lease"), CancellationToken.None);
        await fixture.Executor.Entered.Task;

        result.Accepted.Should().BeTrue();
        fixture.ExecutorLease.DisposeCalls.Should().Be(0);

        releaseExecution.SetResult();
        await fixture.Registry.WaitForIdleAsync(CancellationToken.None);

        fixture.ExecutorLease.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_date_range_is_rejected_before_persistence_or_execution()
    {
        var fixture = new ServiceFixture();

        var result = await fixture.Service.StartAsync(
            NewRequest("run-invalid") with { DateFrom = new DateOnly(2026, 10, 1), DateTo = new DateOnly(2026, 9, 1) },
            CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.RejectionCode.Should().Be(RpcErrorCodes.RpcInvalidParams);
        fixture.Lifecycle.TryCreateCalls.Should().Be(0);
        fixture.Executor.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Stop_is_idempotent_and_cancels_the_active_execution()
    {
        var fixture = new ServiceFixture();
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Execute = async (_, _) => await releaseExecution.Task;

        await fixture.Service.StartAsync(NewRequest("run-1"), CancellationToken.None);
        var runToken = await fixture.Executor.Token.Task;
        var first = await fixture.Service.StopAsync(new RunStopRequest("run-1"), CancellationToken.None);
        var second = await fixture.Service.StopAsync(new RunStopRequest("run-1"), CancellationToken.None);

        first.Accepted.Should().BeTrue();
        first.AlreadyRequested.Should().BeFalse();
        second.Accepted.Should().BeTrue();
        second.AlreadyRequested.Should().BeTrue();
        runToken.IsCancellationRequested.Should().BeTrue();

        releaseExecution.SetResult();
        await fixture.Registry.WaitForIdleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_returns_stable_errors_for_missing_and_terminal_runs()
    {
        var fixture = new ServiceFixture();
        fixture.Lifecycle.Seed(new RunStateSnapshot("run-done", RunLifecycleState.Completed, "lifecycle", null, 1,
            DateTimeOffset.UtcNow, null));

        var missing = await fixture.Service.StopAsync(new RunStopRequest("missing"), CancellationToken.None);
        var terminal = await fixture.Service.StopAsync(new RunStopRequest("run-done"), CancellationToken.None);

        missing.Accepted.Should().BeFalse();
        missing.ErrorCode.Should().Be(RpcErrorCodes.RunNotFound);
        terminal.Accepted.Should().BeFalse();
        terminal.ErrorCode.Should().Be(RpcErrorCodes.RunNotCancellable);
    }

    private static RunStartRequest NewRequest(string runId) => new(
        runId,
        "account-1",
        new DateOnly(2026, 9, 1),
        new DateOnly(2026, 9, 30),
        Path.GetTempPath(),
        "Example Co",
        "standard");

    private sealed class ServiceFixture
    {
        public FakeLifecycleStore Lifecycle { get; } = new();
        public ActiveRunRegistry Registry { get; } = new();
        public FakeExecutor Executor { get; } = new();
        public FakeExecutorLease ExecutorLease { get; }
        public DesktopRunService Service { get; }

        public ServiceFixture()
        {
            var account = new MailboxAccountSnapshot(
                "account-1", "buyer@example.com", "imap.example.com", 993, true,
                "mail.credential", "Buyer", 3, true, "b***@example.com", DateTimeOffset.UtcNow, "INBOX");
            ExecutorLease = new FakeExecutorLease(Executor);
            Service = new DesktopRunService(
                new FakeAccountReader(account),
                new FakeSettingsStore(new UserSettingsSnapshot(
                    7, "account-1", "INBOX", new MailboxFilterRules(false, null, null), new PipelineOptionsPatch(),
                    false, "default", 1, "fingerprint", DateTimeOffset.UtcNow, "Example Co", Path.GetTempPath())),
                new FakeUnitOfWorkFactory(),
                Lifecycle,
                Registry,
                new FakeExecutorLeaseFactory(ExecutorLease),
                TimeProvider.System);
        }
    }

    private sealed class FakeExecutorLease(IDesktopRunExecutor executor) : IDesktopRunExecutorLease
    {
        public IDesktopRunExecutor Executor { get; } = executor;
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeExecutorLeaseFactory(IDesktopRunExecutorLease lease) : IDesktopRunExecutorLeaseFactory
    {
        public ValueTask<IDesktopRunExecutorLease> CreateAsync() => ValueTask.FromResult(lease);
    }

    private sealed class FakeAccountReader(MailboxAccountSnapshot account) : IMailboxAccountReader
    {
        public Task<IReadOnlyList<MailboxAccountSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MailboxAccountSnapshot>>([account]);

        public Task<MailboxConnectionSettings?> FindAsync(string accountId, CancellationToken cancellationToken)
            => Task.FromResult<MailboxConnectionSettings?>(accountId == account.AccountId
                ? new MailboxConnectionSettings(account.AccountId, account.EmailAddress, account.ImapHost,
                    account.ImapPort, account.UseTls, account.CredentialName, account.DefaultMailbox)
                : null);
    }

    private sealed class FakeSettingsStore(UserSettingsSnapshot settings) : IUserSettingsStore
    {
        public Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(settings);
        public Task<UserSettingsSnapshot> UpdateAsync(SettingsUpdateRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task ImportSnapshotAsync(UserSettingsSnapshot snapshot, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeExecutor : IDesktopRunExecutor
    {
        public int Calls { get; private set; }
        public Func<RunStartRequest, CancellationToken, Task> Execute { get; set; } = (_, _) => Task.CompletedTask;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CancellationToken> Token { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(RunStartRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Token.TrySetResult(cancellationToken);
            Entered.TrySetResult();
            return Execute(request, cancellationToken);
        }
    }

    private sealed class FakeUnitOfWorkFactory : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
            => Task.FromResult<IUnitOfWork>(new FakeUnitOfWork(purpose));
    }

    private sealed class FakeUnitOfWork(TransactionPurpose purpose) : IUnitOfWork
    {
        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public TransactionPurpose Purpose { get; } = purpose;
        public bool IsCompleted { get; private set; }
        public Task CommitAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsCompleted = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeLifecycleStore : IRunLifecycleStore
    {
        private readonly Dictionary<string, RunStateSnapshot> _runs = new(StringComparer.Ordinal);
        public int TryCreateCalls { get; private set; }

        public Task<RunStateSnapshot?> FindAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult(_runs.GetValueOrDefault(runId));

        public void Seed(RunStateSnapshot snapshot) => _runs[snapshot.RunId] = snapshot;

        public Task<bool> TryCreateAsync(RunCreationRequest request, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            TryCreateCalls++;
            if (_runs.Values.Any(run => run.State is RunLifecycleState.Created or RunLifecycleState.Running or RunLifecycleState.Recovering))
                return Task.FromResult(false);
            _runs[request.RunId] = new RunStateSnapshot(request.RunId, RunLifecycleState.Created, "admitted", null, 0, null, null);
            return Task.FromResult(true);
        }

        public Task<bool> TryMarkRunningAsync(string runId, string stage, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            if (!_runs.TryGetValue(runId, out var run) || run.State != RunLifecycleState.Created)
                return Task.FromResult(false);
            _runs[runId] = run with { State = RunLifecycleState.Running, Stage = stage };
            return Task.FromResult(true);
        }

        public Task UpdateTerminalStateAsync(RunStateSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task UpdateLastEventSequenceAsync(string runId, long lastEventSequence, IUnitOfWork transaction, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<bool> TryRequestCancellationAsync(string runId, DateTimeOffset requestedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            if (!_runs.TryGetValue(runId, out var run)
                || run.State is not (RunLifecycleState.Created or RunLifecycleState.Running or RunLifecycleState.Recovering)
                || run.CancellationRequestedAtUtc is not null)
                return Task.FromResult(false);
            _runs[runId] = run with { CancellationRequestedAtUtc = requestedAtUtc };
            return Task.FromResult(true);
        }
        public Task<bool> IsCancellationRequestedAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult(_runs.TryGetValue(runId, out var run) && run.CancellationRequestedAtUtc.HasValue);
    }
}