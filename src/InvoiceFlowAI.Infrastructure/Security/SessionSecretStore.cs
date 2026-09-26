using System.Collections.Concurrent;
using InvoiceFlowAI.Application.Security;

namespace InvoiceFlowAI.Infrastructure.Security;

public sealed class SessionSecretStore : ISessionSecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _secrets[name] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(name);
        _secrets.TryGetValue(name, out var value);
        return Task.FromResult(value);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(name);
        _secrets.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}