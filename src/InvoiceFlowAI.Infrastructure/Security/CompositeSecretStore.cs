using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Security;

namespace InvoiceFlowAI.Infrastructure.Security;

public sealed class CompositeSecretStore : ISecretStore
{
    private readonly IPersistentSecretStore _persistentStore;
    private readonly ISessionSecretStore _sessionStore;

    public CompositeSecretStore(IPersistentSecretStore persistentStore, ISessionSecretStore sessionStore)
    {
        _persistentStore = persistentStore ?? throw new ArgumentNullException(nameof(persistentStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    }

    public async Task SaveAsync(string name, string value, CancellationToken cancellationToken)
    {
        await _persistentStore.SaveAsync(name, value, cancellationToken).ConfigureAwait(false);
        await _sessionStore.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var sessionValue = await _sessionStore.GetAsync(name, cancellationToken).ConfigureAwait(false);
        return sessionValue ?? await _persistentStore.GetAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        await _sessionStore.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        await _persistentStore.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
    }
}