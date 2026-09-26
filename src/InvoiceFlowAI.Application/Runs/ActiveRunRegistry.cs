namespace InvoiceFlowAI.Application.Runs;

public sealed class ActiveRunRegistry : IDisposable
{
    private readonly object _sync = new();
    private ActiveRun? _active;
    private bool _disposed;

    public string? ActiveRunId
    {
        get
        {
            lock (_sync)
            {
                return _active?.RunId;
            }
        }
    }

    public event Action<string, Exception>? ExecutionFailed;

    public bool TryStart(string runId, Func<CancellationToken, Task> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(execute);

        ActiveRun activeRun;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
            {
                return false;
            }

            activeRun = new ActiveRun(runId);
            _active = activeRun;
        }

        _ = ExecuteAndReleaseAsync(activeRun, execute);
        return true;
    }

    public bool RequestCancellation(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_sync)
        {
            if (_active is null || !string.Equals(_active.RunId, runId, StringComparison.Ordinal)
                || _active.CancellationRequested)
            {
                return false;
            }

            _active.CancellationRequested = true;
            _active.Cancellation.Cancel();
            return true;
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        Task? completion;
        lock (_sync)
        {
            completion = _active?.Completion.Task;
        }

        if (completion is not null)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Cancellation.Cancel();
        }
    }

    private async Task ExecuteAndReleaseAsync(ActiveRun activeRun, Func<CancellationToken, Task> execute)
    {
        Exception? failure = null;
        try
        {
            await Task.Run(() => execute(activeRun.Cancellation.Token)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            NotifyFailure(activeRun.RunId, exception);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, activeRun))
                {
                    _active = null;
                }
            }

            activeRun.Cancellation.Dispose();
            if (failure is null)
            {
                activeRun.Completion.TrySetResult();
            }
            else
            {
                activeRun.Completion.TrySetException(failure);
                _ = activeRun.Completion.Task.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private void NotifyFailure(string runId, Exception exception)
    {
        var handlers = ExecutionFailed;
        if (handlers is null) return;

        foreach (Action<string, Exception> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(runId, exception);
            }
            catch
            {
                // Failure observers must not prevent run ownership from being released.
            }
        }
    }

    private sealed class ActiveRun(string runId)
    {
        public string RunId { get; } = runId;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationRequested { get; set; }
    }
}