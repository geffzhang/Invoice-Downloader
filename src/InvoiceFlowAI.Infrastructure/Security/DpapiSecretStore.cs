// DpapiSecretStore — Windows-only DPAPI secret store per design §8.
// Stores ciphertext as base64 in a local JSON file. The plaintext NEVER
// leaves this class — call sites only receive a logical name in and out.
//
// Contract:
//   * SaveAsync replaces the file atomically (temp file + rename) so a
//     crash mid-write leaves the previous good copy intact.
//   * Protect uses DataProtectionScope.CurrentUser + an application-derived
//     entropy so a different Windows user or different application on the
//     same machine cannot decrypt.
//   * All decryption failures (base64 corruption, entropy mismatch, user
//     mismatch) are normalized to CryptographicException so the UI can
//     present a stable "credentials unavailable, please re-enter" message.
//   * Outside Windows the constructor immediately throws
//     PlatformNotSupportedException.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoiceFlowAI.Application.Persistence;

namespace InvoiceFlowAI.Infrastructure.Security;

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _filePath;
    private readonly byte[] _entropy;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DpapiSecretStore(string filePath, byte[] entropy)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "DpapiSecretStore requires Windows Data Protection API (DPAPI).");
        }
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _entropy = entropy ?? throw new ArgumentNullException(nameof(entropy));
    }

    public async Task SaveAsync(string name, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required.", nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                _entropy,
                DataProtectionScope.CurrentUser);
            store[name] = Convert.ToBase64String(protectedBytes);
            await WriteStoreUnsafeAsync(store, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required.", nameof(name));
        var store = await ReadStoreUnsafeAsync(cancellationToken).ConfigureAwait(false);
        if (!store.TryGetValue(name, out var base64)) return null;
        if (base64 is null) return null;

        byte[] ciphertext;
        try
        {
            ciphertext = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Stored secret ciphertext is corrupt.");
        }

        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(ciphertext, _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            throw new CryptographicException("Stored secret is not decryptable on this user / machine.", ex);
        }
        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required.", nameof(name));
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (store.Remove(name))
            {
                await WriteStoreUnsafeAsync(store, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<Dictionary<string, string?>> ReadStoreUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var stream = File.OpenRead(_filePath);
        var parsed = await JsonSerializer.DeserializeAsync<Dictionary<string, string?>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return parsed ?? new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    private async Task WriteStoreUnsafeAsync(Dictionary<string, string?> store, CancellationToken cancellationToken)
    {
        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, store, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(_filePath))
        {
            File.Replace(tempPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _filePath);
        }
    }
}