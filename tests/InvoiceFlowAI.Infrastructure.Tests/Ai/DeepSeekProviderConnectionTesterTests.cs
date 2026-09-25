using FluentAssertions;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Infrastructure.Ai;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ai;

public sealed class DeepSeekProviderConnectionTesterTests
{
    [Fact]
    public async Task Test_passes_credential_to_injected_adapter_probe()
    {
        string? receivedCredential = null;
        var tester = new DeepSeekProviderConnectionTester((credential, _) =>
        {
            receivedCredential = credential;
            return Task.CompletedTask;
        });

        var result = await tester.TestAsync("deepseek", "private-api-key", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ProviderId.Should().Be("deepseek");
        receivedCredential.Should().Be("private-api-key");
    }

    [Fact]
    public async Task Unauthorized_adapter_failure_is_mapped_without_raw_details()
    {
        const string secret = "private-api-key";
        var tester = new DeepSeekProviderConnectionTester((_, _) =>
            Task.FromException(new ChatCompletionException(ChatCompletionErrorCode.Unauthorized, $"Rejected {secret}")));

        var result = await tester.TestAsync("deepseek", secret, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be("PROVIDER_AUTH_FAILED");
        result.SafeMessage.Should().NotContain(secret);
        result.SafeMessage.Should().NotContain("Rejected");
    }
}