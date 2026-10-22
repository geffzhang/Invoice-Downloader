using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Tests.TestSupport;

public sealed class FakeRunSession : IRunSession
{
    public string? RunId { get; private set; }
    public void SetRunId(string? runId) => RunId = runId;
}

public sealed class RecordingNavigator : IAppNavigator
{
    public List<string> Requested { get; } = [];

    public Task NavigateAsync(string key, CancellationToken cancellationToken = default)
    {
        Requested.Add(key);
        return Task.CompletedTask;
    }
}

public sealed class NullNotificationService : INotificationService
{
    public List<(string Message, NotificationKind Kind)> Shown { get; } = [];

    public void Show(string message, NotificationKind kind) => Shown.Add((message, kind));
}

public sealed class SynchronousUiDispatcher : IAvaloniaUiDispatcher
{
    public Task<TResult> InvokeAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken)
        => Task.FromResult(action());

    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        action();
        return Task.CompletedTask;
    }

    public void Post(Action action) => action();
}

public sealed class FakeDesktopRunService(RunContextSnapshot context) : IDesktopRunService
{
    public List<RunStartRequest> Started { get; } = [];
    public List<RunStopRequest> Stopped { get; } = [];
    public RunProgressSnapshot Progress { get; set; } = new(
        RunState.Idle,
        false,
        false,
        false,
        0,
        "processing",
        new RunProgressStats(0, 0, 0),
        [],
        null,
        false,
        null,
        null,
        null,
        null);

    public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
        => Task.FromResult(context);

    public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
    {
        Started.Add(request);
        return Task.FromResult(new RunStartResult(true, request.RunId, null));
    }

    public Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
        => Task.FromResult(Progress);

    public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
    {
        Stopped.Add(request);
        return Task.FromResult(new RunStopResult(true, false, null));
    }
}
