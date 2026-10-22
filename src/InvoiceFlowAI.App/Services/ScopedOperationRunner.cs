using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Services;

public sealed class ScopedOperationRunner(IServiceScopeFactory scopeFactory) : IScopedOperationRunner
{
    public async Task<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = scopeFactory.CreateAsyncScope();
        return await operation(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(
        Func<IServiceProvider, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = scopeFactory.CreateAsyncScope();
        await operation(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }
}
