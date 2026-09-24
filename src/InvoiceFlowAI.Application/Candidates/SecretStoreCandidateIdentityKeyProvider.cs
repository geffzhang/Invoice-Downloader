using System.Security.Cryptography;
using InvoiceFlowAI.Application.Persistence;

namespace InvoiceFlowAI.Application.Candidates;

public sealed class SecretStoreCandidateIdentityKeyProvider : ICandidateIdentityKeyProvider
{
    public const string SecretName = "invoiceflow.candidate-identity-hmac.v1";

    private readonly ISecretStore _secretStore;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    public SecretStoreCandidateIdentityKeyProvider(ISecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async Task<CandidateIdentityKey> GetCurrentKeyAsync(CancellationToken cancellationToken)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var encodedKey = await _secretStore.GetAsync(SecretName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(encodedKey))
            {
                var generatedKey = RandomNumberGenerator.GetBytes(32);
                await _secretStore.SaveAsync(SecretName, Convert.ToBase64String(generatedKey), cancellationToken).ConfigureAwait(false);
                return new CandidateIdentityKey("1", generatedKey);
            }

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(encodedKey);
            }
            catch (FormatException)
            {
                throw new CryptographicException("Candidate identity key is unavailable.");
            }

            if (keyBytes.Length != 32)
            {
                CryptographicOperations.ZeroMemory(keyBytes);
                throw new CryptographicException("Candidate identity key is unavailable.");
            }

            return new CandidateIdentityKey("1", keyBytes);
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}