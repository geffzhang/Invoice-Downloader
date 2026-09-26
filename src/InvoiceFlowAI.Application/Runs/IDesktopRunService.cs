using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Runs;

public interface IDesktopRunService
{
    Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken);
    Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken);
    Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken);
    Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken);
}

public interface IDesktopRunExecutor
{
    Task ExecuteAsync(RunStartRequest request, CancellationToken cancellationToken);
}

public interface IDesktopRunExecutorLease : IAsyncDisposable
{
    IDesktopRunExecutor Executor { get; }
}

public interface IDesktopRunExecutorLeaseFactory
{
    ValueTask<IDesktopRunExecutorLease> CreateAsync();
}

public sealed class DesktopRunException(string errorCode, string message)
    : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}