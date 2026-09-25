using InvoiceFlowAI.Application.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Rpc;

public sealed class ScopedDesktopRunExecutorLeaseFactory(IServiceScopeFactory scopeFactory)
    : IDesktopRunExecutorLeaseFactory
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async ValueTask<IDesktopRunExecutorLease> CreateAsync()
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            var executor = scope.ServiceProvider.GetRequiredService<IDesktopRunExecutor>();
            return new ExecutorLease(scope, executor);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class ExecutorLease(AsyncServiceScope scope, IDesktopRunExecutor executor)
        : IDesktopRunExecutorLease
    {
        public IDesktopRunExecutor Executor { get; } = executor;

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}