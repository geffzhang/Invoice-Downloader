using FluentAssertions;
using System.ClientModel;
using System.ClientModel.Primitives;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ai;

public sealed class DeepSeekRequestRedactionTests
{
    [Fact]
    public async Task Provider_failure_does_not_expose_key_prompt_image_or_raw_exception()
    {
        const string apiKey = "private-api-key-sentinel";
        const string systemPrompt = "private-system-prompt-sentinel";
        const string userPrompt = "private-user-prompt-sentinel";
        var imageBytes = System.Text.Encoding.UTF8.GetBytes("private-image-payload-sentinel");
        var service = new DeepSeekChatCompletionService(
            new FixedSecretStore(apiKey),
            (_, _) => Task.FromResult<IChatClient>(new FailingChatClient()));

        var exception = await Assert.ThrowsAsync<ChatCompletionException>(() => service.CompleteVisionAsync(
            new ChatCompletionRequest(systemPrompt, userPrompt),
            [new ChatImagePart("image/png", imageBytes, 1, 1)],
            CancellationToken.None));

        exception.Message.Should().NotContain(apiKey);
        exception.Message.Should().NotContain(systemPrompt);
        exception.Message.Should().NotContain(userPrompt);
        exception.Message.Should().NotContain("private-image-payload-sentinel");
        exception.Message.Should().NotContain("private-provider-exception-sentinel");
    }

    private sealed class FixedSecretStore(string value) : ISecretStore
    {
        public Task SaveAsync(string name, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(value);
        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new ClientResultException(
                "private-provider-exception-sentinel",
                new TestPipelineResponse(),
                null));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestPipelineResponse : PipelineResponse
    {
        public override int Status => 500;
        public override string ReasonPhrase => string.Empty;
        protected override PipelineResponseHeaders HeadersCore => new TestPipelineResponseHeaders();
        public override Stream? ContentStream { get; set; } = Stream.Null;
        public override BinaryData Content => BinaryData.FromString(string.Empty);
        public override BinaryData BufferContent(CancellationToken cancellationToken) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Content);
        public override void Dispose() { }
    }

    private sealed class TestPipelineResponseHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string> values)
        {
            values = Array.Empty<string>();
            return false;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
    }
}