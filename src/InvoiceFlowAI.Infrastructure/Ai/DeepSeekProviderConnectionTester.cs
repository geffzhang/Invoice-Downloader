using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Infrastructure.Ai;

public sealed class DeepSeekProviderConnectionTester : IProviderConnectionTester
{
    private readonly Func<string, CancellationToken, Task> _probe;

    public DeepSeekProviderConnectionTester(Func<string, CancellationToken, Task>? probe = null)
        => _probe = probe ?? ProbeWithAdapterAsync;

    public async Task<ProviderTestResult> TestAsync(
        string providerId,
        string credential,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(providerId, "deepseek", StringComparison.Ordinal))
        {
            return new ProviderTestResult(providerId, false, "PROVIDER_UNSUPPORTED", "Provider is not supported.");
        }

        try
        {
            await _probe(credential, cancellationToken).ConfigureAwait(false);
            return new ProviderTestResult(providerId, true, SafeMessage: "Provider connection succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChatCompletionException exception)
        {
            return exception.Code switch
            {
                ChatCompletionErrorCode.Unauthorized => new ProviderTestResult(providerId, false, "PROVIDER_AUTH_FAILED", "The API key was rejected."),
                ChatCompletionErrorCode.RateLimited => new ProviderTestResult(providerId, false, "PROVIDER_RATE_LIMITED", "The provider is temporarily rate limited."),
                ChatCompletionErrorCode.Timeout => new ProviderTestResult(providerId, false, "PROVIDER_TIMEOUT", "The provider did not respond in time."),
                _ => new ProviderTestResult(providerId, false, "PROVIDER_TEST_FAILED", "Provider connection test failed."),
            };
        }
        catch
        {
            return new ProviderTestResult(providerId, false, "PROVIDER_TEST_FAILED", "Provider connection test failed.");
        }
    }

    private static async Task ProbeWithAdapterAsync(string credential, CancellationToken cancellationToken)
    {
        var adapter = new DeepSeekChatCompletionService(
            new SingleValueSecretStore(credential),
            maxAttempts: 1,
            requestTimeout: TimeSpan.FromSeconds(15));
        await adapter.CompleteTextAsync(
            new ChatCompletionRequest("Respond with a short connection acknowledgement.", "Reply with OK.", MaxOutputTokens: 1),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class SingleValueSecretStore(string value) : ISecretStore
    {
        public Task SaveAsync(string name, string secret, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult<string?>(value);

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}