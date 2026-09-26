using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Security;

public sealed class SecretApplicationService : ISecretRetentionStore
{
    public const string ImapAuthCodeName = "mail.imap.auth-code";
    public const string DeepSeekApiKeyName = "deepseek.api-key";

    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        ImapAuthCodeName,
        DeepSeekApiKeyName,
    };

    private readonly IPersistentSecretStore _persistentStore;
    private readonly ISessionSecretStore _sessionStore;

    public SecretApplicationService(IPersistentSecretStore persistentStore, ISessionSecretStore sessionStore)
    {
        _persistentStore = persistentStore ?? throw new ArgumentNullException(nameof(persistentStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    }

    public async Task<SecretMutationResult> SetAsync(SecretSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AllowedNames.Contains(request.Name))
        {
            throw new ArgumentException("Secret name is not allowed.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            throw new ArgumentException("Secret value cannot be empty.", nameof(request));
        }

        switch (request.Retention)
        {
            case SecretRetention.Persistent:
                await _persistentStore.SaveAsync(request.Name, request.Value, cancellationToken).ConfigureAwait(false);
                await _sessionStore.DeleteAsync(request.Name, cancellationToken).ConfigureAwait(false);
                return new SecretMutationResult(request.Name, Configured: true, Persistent: true);

            case SecretRetention.Session:
                await _persistentStore.DeleteAsync(request.Name, cancellationToken).ConfigureAwait(false);
                await _sessionStore.SaveAsync(request.Name, request.Value, cancellationToken).ConfigureAwait(false);
                return new SecretMutationResult(request.Name, Configured: true, Persistent: false);

            default:
                throw new ArgumentOutOfRangeException(nameof(request), "Secret retention value is not supported.");
        }
    }
}