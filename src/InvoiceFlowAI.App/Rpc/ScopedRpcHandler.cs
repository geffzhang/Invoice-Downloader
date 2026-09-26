using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Rpc;

public sealed class ScopedRpcHandler<THandler>(IServiceScopeFactory scopeFactory, string method) : IRpcHandler
    where THandler : class, IRpcHandler
{
    public string Method { get; } = method;

    public async Task<RpcHandlerResult> HandleAsync(
        InvoiceFlowAI.Contracts.Rpc.RpcRequest<System.Text.Json.JsonElement?> request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        if (!string.Equals(handler.Method, Method, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Scoped RPC handler method mismatch.");
        }

        return await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }
}