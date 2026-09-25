// ISecretStore per design §8. Application-layer facade in front of the
// Windows DPAPI implementation in Infrastructure. The interface never
// exposes platform types so test fakes can swap in a deterministic store.

namespace InvoiceFlowAI.Application.Persistence;

/// <summary>
/// Retrieves named secrets, checking process-session values before the
/// persistent protected store. Provider secrets (DeepSeek key, IMAP
/// authorization codes) must only ever travel through this abstraction.
/// DTOs, log entries, audit payloads, Recipe JSON and SQLite JSON columns
/// are prohibited from containing the returned plaintext — only the logical
/// name is allowed to leak past the call boundary.
/// </summary>
public interface ISecretStore
{
    // Direct writes are persistent. Retention-aware writes go through
    // ISecretRetentionStore so session-only values cannot be persisted by mistake.
    Task SaveAsync(string name, string value, CancellationToken cancellationToken);

    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

public interface IPersistentSecretStore : ISecretStore
{
}