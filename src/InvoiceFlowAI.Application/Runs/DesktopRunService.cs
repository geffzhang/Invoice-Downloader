using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Runs;

public sealed class DesktopRunService : IDesktopRunService
{
    private readonly IMailboxAccountReader _accounts;
    private readonly IUserSettingsStore _settings;
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly IRunLifecycleStore _lifecycleStore;
    private readonly ActiveRunRegistry _activeRuns;
    private readonly IDesktopRunExecutorLeaseFactory _executorLeaseFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _stopGate = new(1, 1);

    public DesktopRunService(
        IMailboxAccountReader accounts,
        IUserSettingsStore settings,
        IUnitOfWorkFactory uowFactory,
        IRunLifecycleStore lifecycleStore,
        ActiveRunRegistry activeRuns,
        IDesktopRunExecutorLeaseFactory executorLeaseFactory,
        TimeProvider timeProvider)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _lifecycleStore = lifecycleStore ?? throw new ArgumentNullException(nameof(lifecycleStore));
        _activeRuns = activeRuns ?? throw new ArgumentNullException(nameof(activeRuns));
        _executorLeaseFactory = executorLeaseFactory ?? throw new ArgumentNullException(nameof(executorLeaseFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(item => item.AccountId == settings.CurrentAccountId);
        return new RunContextSnapshot(
            ExplicitRunContext: false,
            ControlledRun: false,
            AutostartEnabled: false,
            AutostartDelayMs: 0,
            RunId: _activeRuns.ActiveRunId,
            LockedOutputPath: settings.LastOutputDirectory,
            LockedDateFrom: null,
            LockedDateTo: null,
            LockedEmail: account?.EmailAddress,
            AccountId: settings.CurrentAccountId);
    }

    public async Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValid(request))
        {
            return new RunStartResult(false, request.RunId ?? string.Empty, RpcErrorCodes.RpcInvalidParams);
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeRuns.ActiveRunId is not null)
            {
                return new RunStartResult(false, request.RunId, RpcErrorCodes.RunAlreadyActive);
            }

            var connection = await _accounts.FindAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                return new RunStartResult(false, request.RunId, RpcErrorCodes.RunConfigurationSnapshotMissing);
            }

            var account = (await _accounts.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.AccountId == request.AccountId);
            if (account is null)
            {
                return new RunStartResult(false, request.RunId, RpcErrorCodes.RunConfigurationSnapshotMissing);
            }

            var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_activeRuns.TryStart(request.RunId, async token =>
                {
                    if (await admitted.Task.ConfigureAwait(false))
                    {
                        await using var executorLease = await _executorLeaseFactory.CreateAsync().ConfigureAwait(false);
                        await executorLease.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
                    }
                }))
            {
                return new RunStartResult(false, request.RunId, RpcErrorCodes.RunAlreadyActive);
            }

            try
            {
                await using var uow = await _uowFactory.BeginAsync(TransactionPurpose.RunCreate, cancellationToken)
                    .ConfigureAwait(false);
                var created = await _lifecycleStore.TryCreateAsync(
                    new RunCreationRequest(
                        request.RunId,
                        request.DateFrom,
                        request.DateTo.AddDays(1),
                        request.AccountId,
                        account.Revision,
                        connection.DefaultMailbox ?? settings.DefaultMailbox,
                        Path.GetFullPath(request.OutputDirectory),
                        settings.Revision,
                        settings.RuleSetId,
                        settings.RuleSetVersion,
                        settings.ConfigurationFingerprint,
                        "2026-09-23-v1",
                        _timeProvider.GetUtcNow()),
                    uow,
                    cancellationToken).ConfigureAwait(false);
                if (!created)
                {
                    await uow.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    admitted.TrySetResult(false);
                    await _activeRuns.WaitForIdleAsync(CancellationToken.None).ConfigureAwait(false);
                    return new RunStartResult(false, request.RunId, RpcErrorCodes.RunAlreadyActive);
                }

                if (!await _lifecycleStore.TryMarkRunningAsync(
                    request.RunId,
                    "initializing",
                    uow,
                    cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Newly admitted run could not enter Running state.");
                }

                await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                admitted.TrySetResult(true);
                return new RunStartResult(true, request.RunId, null);
            }
            catch
            {
                admitted.TrySetResult(false);
                await _activeRuns.WaitForIdleAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
    {
        var requestedRunId = string.IsNullOrWhiteSpace(runId) ? _activeRuns.ActiveRunId : runId;
        if (string.IsNullOrWhiteSpace(requestedRunId)) return EmptyProgress();

        var state = await _lifecycleStore.FindAsync(requestedRunId, cancellationToken).ConfigureAwait(false);
        if (state is null) throw new DesktopRunException(RpcErrorCodes.RunNotFound, "Run was not found.");

        var runState = state.State switch
        {
            RunLifecycleState.Created or RunLifecycleState.Running or RunLifecycleState.Recovering => RunState.Running,
            RunLifecycleState.Cancelled => RunState.Cancelled,
            RunLifecycleState.Failed => RunState.Failed,
            _ => RunState.Completed,
        };
        var isRunning = runState is RunState.Running or RunState.Stopping;
        var stopRequested = state.CancellationRequestedAtUtc is not null;
        return new RunProgressSnapshot(
            runState,
            isRunning,
            isRunning && !stopRequested,
            stopRequested,
            0,
            state.Stage,
            new RunProgressStats(0, 0, 0),
            Array.Empty<RunLogEntry>(),
            null,
            false,
            null,
            null,
            null,
            null);
    }

    public async Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _lifecycleStore.FindAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return new RunStopResult(false, false, RpcErrorCodes.RunNotFound);
            }
            if (state.State is not (RunLifecycleState.Created or RunLifecycleState.Running or RunLifecycleState.Recovering)
                || !string.Equals(_activeRuns.ActiveRunId, request.RunId, StringComparison.Ordinal))
            {
                return new RunStopResult(false, false, RpcErrorCodes.RunNotCancellable);
            }
            if (state.CancellationRequestedAtUtc is not null)
            {
                _activeRuns.RequestCancellation(request.RunId);
                return new RunStopResult(true, true, null);
            }

            await using var uow = await _uowFactory.BeginAsync(TransactionPurpose.RunTransition, cancellationToken)
                .ConfigureAwait(false);
            var requested = await _lifecycleStore.TryRequestCancellationAsync(
                request.RunId,
                _timeProvider.GetUtcNow(),
                uow,
                cancellationToken).ConfigureAwait(false);
            if (!requested)
            {
                await uow.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var latest = await _lifecycleStore.FindAsync(request.RunId, cancellationToken).ConfigureAwait(false);
                if (latest is not null
                    && latest.State is RunLifecycleState.Created or RunLifecycleState.Running or RunLifecycleState.Recovering
                    && latest.CancellationRequestedAtUtc is not null)
                {
                    _activeRuns.RequestCancellation(request.RunId);
                    return new RunStopResult(true, true, null);
                }
                return new RunStopResult(false, false, RpcErrorCodes.RunNotCancellable);
            }
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            _activeRuns.RequestCancellation(request.RunId);
            return new RunStopResult(true, false, null);
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private static bool IsValid(RunStartRequest request) =>
        !string.IsNullOrWhiteSpace(request.RunId)
        && !string.IsNullOrWhiteSpace(request.AccountId)
        && request.DateFrom <= request.DateTo
        && request.DateTo < DateOnly.MaxValue
        && !string.IsNullOrWhiteSpace(request.CompanyName)
        && !string.IsNullOrWhiteSpace(request.RunMode)
        && !string.IsNullOrWhiteSpace(request.OutputDirectory)
        && Path.IsPathFullyQualified(request.OutputDirectory);

    private static RunProgressSnapshot EmptyProgress() => new(
        RunState.Idle,
        false,
        false,
        false,
        0,
        "等待任务开始",
        new RunProgressStats(0, 0, 0),
        Array.Empty<RunLogEntry>(),
        null,
        false,
        null,
        null,
        null,
        null);
}