// Verifies DeepSeekChatCompletionService image-size enforcement (Task 8).
// The 401 / 429 / timeout error codes are exercised in the
// EmailBodyReceiptService tests via the fake chat service — the real
// mapping from Microsoft.Extensions.AI exceptions lives in the wiring
// registered in Task 11 and is covered by integration tests there.

using FluentAssertions;
using System.ClientModel;
using System.ClientModel.Primitives;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ai;

public sealed class DeepSeekChatCompletionServiceTests
{
    [Fact]
        public async Task Text_request_maps_role_prompt_model_temperature_and_usage()
        {
            var client = new FakeChatClient("{\"ok\":true}", "deepseek-flash", 12, 5);
            var service = new DeepSeekChatCompletionService(clientFactory: (_, _) => Task.FromResult<IChatClient>(client));

            var result = await service.CompleteTextAsync(
                new ChatCompletionRequest("system-private", "user-private", Temperature: 0.1, MaxOutputTokens: 200),
                CancellationToken.None);

            result.Text.Should().Be("{\"ok\":true}");
            result.Model.Should().Be("deepseek-flash");
            result.PromptTokens.Should().Be(12);
            result.CompletionTokens.Should().Be(5);
            client.LastMessages.Should().HaveCount(2);
            client.LastMessages[0].Role.Value.Should().Be("system");
            client.LastMessages[0].Text.Should().Be("system-private");
            client.LastMessages[1].Role.Value.Should().Be("user");
            client.LastMessages[1].Text.Should().Be("user-private");
            client.LastOptions!.Temperature.Should().Be(0.1f);
        }

        [Fact]
        public async Task Vision_request_preserves_image_order_and_places_images_in_user_content()
        {
            var client = new FakeChatClient("ok", "deepseek-flash", 2, 1);
            var service = new DeepSeekChatCompletionService(clientFactory: (_, _) => Task.FromResult<IChatClient>(client));
            var images = new[]
            {
                new ChatImagePart("image/png", new byte[] { 1, 2 }, 10, 20),
                new ChatImagePart("image/jpeg", new byte[] { 3, 4, 5 }, 30, 40),
            };

            await service.CompleteVisionAsync(new ChatCompletionRequest("system", "inspect"), images, CancellationToken.None);

            client.LastMessages.Should().HaveCount(2);
            client.LastMessages[0].Role.Value.Should().Be("system");
            client.LastMessages[0].Text.Should().Be("system");
            client.LastMessages[1].Role.Value.Should().Be("user");
            client.LastMessages[1].Contents.Should().HaveCount(3);
            client.LastMessages[1].Contents[0].Should().BeOfType<TextContent>().Which.Text.Should().Be("inspect");
            client.LastMessages[1].Contents[1].Should().BeOfType<DataContent>().Which.Data.ToArray().Should().Equal(1, 2);
            client.LastMessages[1].Contents[2].Should().BeOfType<DataContent>().Which.Data.ToArray().Should().Equal(3, 4, 5);
        }

        [Fact]
        public async Task Vision_request_rejects_oversized_image()
        {
            var client = new FakeChatClient("unused", "deepseek-flash", 0, 0);
            var service = new DeepSeekChatCompletionService(
                maxImageBytes: 1024,
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client));

            var exception = await Assert.ThrowsAsync<ChatCompletionException>(() => service.CompleteVisionAsync(
                new ChatCompletionRequest("sys", "user"),
                [new ChatImagePart("image/png", new byte[2048], 1, 1)],
                CancellationToken.None));

            exception.Code.Should().Be(ChatCompletionErrorCode.ImageTooLarge);
            client.CallCount.Should().Be(0);
        }

    [Fact]
    public void Default_max_image_bytes_is_4MiB()
    {
        DeepSeekChatCompletionService.DefaultMaxImageBytes.Should().Be(4 * 1024 * 1024);
    }

        [Theory]
        [InlineData("text/plain", 1, 1)]
        [InlineData("image/png", 0, 1)]
        [InlineData("image/png", 1, 8193)]
        public async Task Vision_request_rejects_unsupported_mime_or_dimensions(string mimeType, int width, int height)
        {
            var client = new FakeChatClient("unused", "deepseek-flash", 0, 0);
            var service = new DeepSeekChatCompletionService(clientFactory: (_, _) => Task.FromResult<IChatClient>(client));

            var exception = await Assert.ThrowsAsync<ChatCompletionException>(() => service.CompleteVisionAsync(
                new ChatCompletionRequest("sys", "user"),
                [new ChatImagePart(mimeType, new byte[] { 1 }, width, height)],
                CancellationToken.None));

            exception.Code.Should().Be(ChatCompletionErrorCode.UnsupportedImage);
            client.CallCount.Should().Be(0);
        }

        [Fact]
        public async Task Rate_limited_request_retries_within_attempt_bound()
        {
            var client = new FakeChatClient("ok", "deepseek-flash", 2, 1)
            {
                FailuresRemaining = 2,
                Failure = new HttpRequestException("private-provider-detail", null, System.Net.HttpStatusCode.TooManyRequests),
            };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 3);

            var result = await service.CompleteTextAsync(new ChatCompletionRequest("system", "user"), CancellationToken.None);

            result.Text.Should().Be("ok");
            client.CallCount.Should().Be(3);
        }

        [Fact]
        public async Task Connection_failure_retries_within_attempt_bound()
        {
            var client = new FakeChatClient("ok", "deepseek-flash", 1, 1)
            {
                FailuresRemaining = 1,
                Failure = new HttpRequestException("private-network-detail"),
            };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 2);

            var result = await service.CompleteTextAsync(
                new ChatCompletionRequest("system", "user"), CancellationToken.None);

            result.Text.Should().Be("ok");
            client.CallCount.Should().Be(2);
        }

        [Fact]
        public async Task Authentication_failure_is_not_retried_and_provider_text_is_redacted()
        {
            var client = new FakeChatClient("unused", "deepseek-flash", 0, 0)
            {
                FailuresRemaining = 3,
                Failure = new HttpRequestException("private-provider-detail", null, System.Net.HttpStatusCode.Unauthorized),
            };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 4);

            var exception = await Assert.ThrowsAsync<ChatCompletionException>(() => service.CompleteTextAsync(
                new ChatCompletionRequest("system", "user"), CancellationToken.None));

            exception.Code.Should().Be(ChatCompletionErrorCode.Unauthorized);
            exception.Message.Should().NotContain("private-provider-detail");
            client.CallCount.Should().Be(1);
        }

        [Fact]
        public async Task Client_result_rate_limit_maps_status_and_retry_after_without_provider_text()
        {
            var client = new FakeChatClient("unused", "deepseek-flash", 0, 0)
            {
                FailuresRemaining = 1,
                Failure = new ClientResultException(
                    "private-provider-detail",
                    new FakePipelineResponse(429, "7"),
                    null),
            };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 1);

            var exception = await Assert.ThrowsAsync<ChatCompletionException>(() => service.CompleteTextAsync(
                new ChatCompletionRequest("system", "user"), CancellationToken.None));

            exception.Code.Should().Be(ChatCompletionErrorCode.RateLimited);
            exception.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
            exception.Message.Should().NotContain("private-provider-detail");
            client.CallCount.Should().Be(1);
        }

        [Theory]
        [InlineData(401)]
        [InlineData(403)]
        public async Task Client_result_auth_failures_are_redacted_and_not_retried(int status)
        {
            var client = new FakeChatClient("unused", "deepseek-flash", 0, 0)
            {
                FailuresRemaining = 2,
                Failure = new ClientResultException(
                    "private-provider-detail",
                    new FakePipelineResponse(status, null),
                    null),
            };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 3);

            var exception = await Assert.ThrowsAsync<ChatCompletionException>(() => service.CompleteTextAsync(
                new ChatCompletionRequest("system", "user"), CancellationToken.None));

            exception.Code.Should().Be(ChatCompletionErrorCode.Unauthorized);
            exception.Message.Should().NotContain("private-provider-detail");
            client.CallCount.Should().Be(1);
        }

        [Fact]
        public async Task Zero_retry_after_allows_rate_limited_request_to_retry()
        {
            var client = new FakeChatClient("ok", "deepseek-flash", 1, 1)
            {
                FailuresRemaining = 1,
                Failure = new ClientResultException("private", new FakePipelineResponse(429, "0"), null),
            };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 2);

            var result = await service.CompleteTextAsync(new ChatCompletionRequest("sys", "user"), CancellationToken.None);

            result.Text.Should().Be("ok");
            client.CallCount.Should().Be(2);
        }

        [Fact]
        public async Task Attempt_timeout_does_not_shorten_retry_after_wait()
        {
            var client = new FakeChatClient("ok", "deepseek-flash", 1, 1)
            {
                FailuresRemaining = 1,
                Failure = new ClientResultException("private", new FakePipelineResponse(429, "1"), null),
            };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 2,
                requestTimeout: TimeSpan.FromMilliseconds(40));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var result = await service.CompleteTextAsync(
                new ChatCompletionRequest("sys", "user"), CancellationToken.None);

            result.Text.Should().Be("ok");
            client.CallCount.Should().Be(2);
            stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        }

        [Fact]
        public async Task Total_request_size_is_checked_before_calling_provider()
        {
            var client = new FakeChatClient("unused", "deepseek-flash", 0, 0);
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxRequestBytes: 8);

            var exception = await Assert.ThrowsAsync<ChatCompletionException>(() => service.CompleteTextAsync(
                new ChatCompletionRequest("system prompt", "user prompt"), CancellationToken.None));

            exception.Code.Should().Be(ChatCompletionErrorCode.RequestTooLarge);
            client.CallCount.Should().Be(0);
        }

        [Fact]
        public async Task Caller_cancellation_is_propagated_without_retry()
        {
            var client = new FakeChatClient("unused", "deepseek-flash", 0, 0) { TimeoutFirstCall = true };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client), maxAttempts: 3,
                requestTimeout: TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompleteTextAsync(
                new ChatCompletionRequest("system", "user"), cancellation.Token));

            client.CallCount.Should().Be(1);
        }

        [Fact]
        public void Infrastructure_registration_resolves_chat_service_from_secret_store()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));
            services.AddInvoiceFlowInfrastructure();
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IChatCompletionService>().Should().BeOfType<DeepSeekChatCompletionService>();
        }

        [Fact]
        public async Task Timed_out_attempt_retries_and_succeeds_within_attempt_bound()
        {
            var client = new FakeChatClient("ok", "deepseek-flash", 1, 1) { TimeoutFirstCall = true };
            var service = new DeepSeekChatCompletionService(
                clientFactory: (_, _) => Task.FromResult<IChatClient>(client),
                maxAttempts: 2,
                requestTimeout: TimeSpan.FromMilliseconds(20));

            var result = await service.CompleteTextAsync(
                new ChatCompletionRequest("system", "user"), CancellationToken.None);

            result.Text.Should().Be("ok");
            client.CallCount.Should().Be(2);
        }

        [Fact]
        public async Task Secret_is_retrieved_for_each_request_and_is_passed_only_to_client_factory()
        {
            var secretStore = new FakeSecretStore("private-api-key-sentinel");
            var receivedKeys = new List<string>();
            var client = new FakeChatClient("ok", "deepseek-flash", 1, 1);
            var service = new DeepSeekChatCompletionService(
                secretStore,
                (key, _) =>
                {
                    receivedKeys.Add(key);
                    return Task.FromResult<IChatClient>(client);
                });

            await service.CompleteTextAsync(new ChatCompletionRequest("system", "user"), CancellationToken.None);
            await service.CompleteTextAsync(new ChatCompletionRequest("system", "user"), CancellationToken.None);

            secretStore.ReadCount.Should().Be(2);
            receivedKeys.Should().OnlyContain(key => key == "private-api-key-sentinel");
        }

        private sealed class FakeChatClient(string responseText, string modelId, int inputTokens, int outputTokens) : IChatClient
        {
            public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = Array.Empty<ChatMessage>();
            public ChatOptions? LastOptions { get; private set; }
            public int CallCount { get; private set; }
            public int FailuresRemaining { get; set; }
            public Exception? Failure { get; set; }
            public bool TimeoutFirstCall { get; set; }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                LastMessages = messages.ToArray();
                LastOptions = options;
                if (FailuresRemaining > 0)
                {
                    FailuresRemaining--;
                    throw Failure!;
                }
                if (TimeoutFirstCall && CallCount == 1)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))
                {
                    ModelId = modelId,
                    Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
                };
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }

        private sealed class FakeSecretStore(string? value) : ISecretStore
        {
            public int ReadCount { get; private set; }
            public Task SaveAsync(string name, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            {
                ReadCount++;
                name.Should().Be("deepseek.api-key");
                return Task.FromResult(value);
            }
            public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakePipelineResponse(int status, string? retryAfter) : PipelineResponse
        {
            public override int Status => status;
            public override string ReasonPhrase => string.Empty;
            protected override PipelineResponseHeaders HeadersCore => new FakePipelineResponseHeaders(retryAfter);
            public override Stream? ContentStream { get; set; } = Stream.Null;
            public override BinaryData Content => BinaryData.FromString(string.Empty);
            public override BinaryData BufferContent(CancellationToken cancellationToken) => Content;
            public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Content);
            public override void Dispose() { }
        }

        private sealed class FakePipelineResponseHeaders(string? retryAfter) : PipelineResponseHeaders
        {
            public override bool TryGetValue(string name, out string value)
            {
                value = name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase) ? retryAfter ?? string.Empty : string.Empty;
                return value.Length > 0;
            }

            public override bool TryGetValues(string name, out IEnumerable<string> values)
            {
                var found = TryGetValue(name, out var value);
                values = found ? [value] : Array.Empty<string>();
                return found;
            }

            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                if (TryGetValue("Retry-After", out var value))
                {
                    yield return new KeyValuePair<string, string>("Retry-After", value);
                }
            }
        }
}
