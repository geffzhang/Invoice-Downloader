// DeepSeek / OpenAI-compatible chat completion service adapter.
// Maps Microsoft.Extensions.AI exceptions and HTTP status codes to
// ChatCompletionErrorCode so the application layer can surface a
// stable failure reason without leaking provider exception text or
// full URLs. Image bytes larger than the configured limit are
// rejected before the request leaves the process.

using System.ClientModel;
using System.Net;
using System.Net.Http;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Persistence;
using Microsoft.Extensions.AI;
using OpenAI;

namespace InvoiceFlowAI.Infrastructure.Ai;

public sealed class DeepSeekChatCompletionService : IChatCompletionService
{
    public const int DefaultMaxImageBytes = 4 * 1024 * 1024;
    public const int DefaultMaxRequestBytes = 48 * 1024 * 1024;
    public const string DefaultModel = "deepseek-flash";
    public static readonly Uri DefaultEndpoint = new("https://api.deepseek.com");

    private static readonly HashSet<string> SupportedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp",
    };

    private readonly ISecretStore? _secretStore;
    private readonly Func<string, CancellationToken, Task<IChatClient>>? _clientFactory;
    private readonly int _maxImageBytes;
    private readonly int _maxRequestBytes;
    private readonly int _maxAttempts;
    private readonly TimeSpan _requestTimeout;

    public DeepSeekChatCompletionService(
        ISecretStore? secretStore = null,
        Func<string, CancellationToken, Task<IChatClient>>? clientFactory = null,
        int? maxImageBytes = null,
        int maxRequestBytes = DefaultMaxRequestBytes,
        int maxAttempts = 3,
        TimeSpan? requestTimeout = null)
    {
        _secretStore = secretStore;
        _clientFactory = clientFactory;
        _maxImageBytes = maxImageBytes ?? DefaultMaxImageBytes;
        _maxRequestBytes = maxRequestBytes;
        _maxAttempts = maxAttempts;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        if (_maxImageBytes <= 0 || _maxRequestBytes <= 0 || _maxAttempts <= 0 || _requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxImageBytes), "AI request limits must be positive.");
        }
        if (_secretStore is null && _clientFactory is null)
        {
            throw new ArgumentException("A secret store or test client factory is required.");
        }
    }

    public Task<ChatCompletionResult> CompleteTextAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken) => CompleteAsync(request, Array.Empty<ChatImagePart>(), cancellationToken);

    public Task<ChatCompletionResult> CompleteVisionAsync(
        ChatCompletionRequest request,
        IReadOnlyList<ChatImagePart> images,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0 || images.Count > 2)
        {
            throw new ChatCompletionException(ChatCompletionErrorCode.UnsupportedImage, "Image count is outside the supported limit.");
        }

        foreach (var image in images)
        {
            ValidateImage(image);
        }

        return CompleteAsync(request, images, cancellationToken);
    }

    private async Task<ChatCompletionResult> CompleteAsync(
        ChatCompletionRequest request,
        IReadOnlyList<ChatImagePart> images,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequestSize(request, images);

        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                var apiKey = _secretStore is null
                    ? "test-client"
                    : await _secretStore.GetAsync("deepseek.api-key", timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new ChatCompletionException(ChatCompletionErrorCode.Unauthorized, "AI credentials are unavailable.");
                }

                using var client = await CreateClientAsync(apiKey, timeout.Token).ConfigureAwait(false);
                var messages = CreateMessages(request, images);
                var options = new ChatOptions
                {
                    ModelId = request.Model ?? DefaultModel,
                    Temperature = (float)(request.Temperature ?? 0.1),
                    MaxOutputTokens = request.MaxOutputTokens,
                };
                var response = await client.GetResponseAsync(messages, options, timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    throw new ChatCompletionException(ChatCompletionErrorCode.InvalidResponse, "AI returned an empty response.");
                }

                return new ChatCompletionResult(
                    response.Text,
                    response.ModelId ?? options.ModelId ?? DefaultModel,
                    checked((int)(response.Usage?.InputTokenCount ?? 0)),
                    checked((int)(response.Usage?.OutputTokenCount ?? 0)));
            }
            catch (ChatCompletionException ex) when (ShouldRetry(ex.Code, attempt))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (MapHttpFailure(ex) is { } failure && ShouldRetry(failure.Code, attempt))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (ClientResultException ex) when (MapClientResultFailure(ex) is { } failure && ShouldRetry(failure.Code, attempt))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken, failure.RetryAfter).ConfigureAwait(false);
            }
            catch (ClientResultException ex)
            {
                throw MapClientResultFailure(ex);
            }
            catch (HttpRequestException ex)
            {
                throw MapHttpFailure(ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _maxAttempts)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ChatCompletionException(ChatCompletionErrorCode.Timeout, "AI request timed out.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ChatCompletionException)
            {
                throw;
            }
            catch
            {
                throw new ChatCompletionException(ChatCompletionErrorCode.Unknown, "AI request failed.");
            }
        }
    }

    private async Task<IChatClient> CreateClientAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (_clientFactory is not null)
        {
            return await _clientFactory(apiKey, cancellationToken).ConfigureAwait(false);
        }

        var options = new OpenAIClientOptions { Endpoint = DefaultEndpoint, NetworkTimeout = _requestTimeout };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(DefaultModel)
            .AsIChatClient();
    }

    private static IList<ChatMessage> CreateMessages(ChatCompletionRequest request, IReadOnlyList<ChatImagePart> images)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
        if (images.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.User, request.UserPrompt));
            return messages;
        }

        var userContent = new List<AIContent> { new TextContent(request.UserPrompt) };
        userContent.AddRange(images.Select(image => (AIContent)new DataContent(image.Bytes, image.ContentType)));
        messages.Add(new ChatMessage(ChatRole.User, userContent));
        return messages;
    }

    private void ValidateImage(ChatImagePart image)
    {
        if (!SupportedImageTypes.Contains(image.ContentType)
            || image.Width <= 0 || image.Height <= 0 || image.Width > 8192 || image.Height > 8192)
        {
            throw new ChatCompletionException(ChatCompletionErrorCode.UnsupportedImage, "Image type or dimensions are unsupported.");
        }
        if (image.Bytes.Length == 0 || image.Bytes.Length > _maxImageBytes)
        {
            throw new ChatCompletionException(ChatCompletionErrorCode.ImageTooLarge, "Image exceeds the configured byte limit.");
        }
    }

    private void ValidateRequestSize(ChatCompletionRequest request, IReadOnlyList<ChatImagePart> images)
    {
        long estimatedBytes = System.Text.Encoding.UTF8.GetByteCount(request.SystemPrompt)
            + System.Text.Encoding.UTF8.GetByteCount(request.UserPrompt);
        foreach (var image in images)
        {
            estimatedBytes = checked(estimatedBytes + (((long)image.Bytes.Length + 2) / 3 * 4));
        }
        if (estimatedBytes > _maxRequestBytes)
        {
            throw new ChatCompletionException(ChatCompletionErrorCode.RequestTooLarge, "AI request exceeds the configured byte limit.");
        }
    }

    private static ChatCompletionException MapHttpFailure(HttpRequestException exception) =>
        MapStatusCode(exception.StatusCode, retryAfter: null);

    private static ChatCompletionException MapClientResultFailure(ClientResultException exception)
    {
        TimeSpan? retryAfter = null;
        var response = exception.GetRawResponse();
        if (response?.Headers.TryGetValue("Retry-After", out var value) == true)
        {
            if (int.TryParse(value, out var seconds) && seconds >= 0)
            {
                retryAfter = TimeSpan.FromSeconds(Math.Min(seconds, 30));
            }
            else if (DateTimeOffset.TryParse(value, out var retryAt))
            {
                retryAfter = TimeSpan.FromMilliseconds(Math.Clamp((retryAt - DateTimeOffset.UtcNow).TotalMilliseconds, 0, 30_000));
            }
        }

        return MapStatusCode((HttpStatusCode)exception.Status, retryAfter);
    }

    private static ChatCompletionException MapStatusCode(HttpStatusCode? statusCode, TimeSpan? retryAfter) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new ChatCompletionException(ChatCompletionErrorCode.Unauthorized, "AI credentials were rejected."),
        HttpStatusCode.TooManyRequests =>
            new ChatCompletionException(ChatCompletionErrorCode.RateLimited, "AI request was rate limited.", retryAfter),
        null or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            new ChatCompletionException(ChatCompletionErrorCode.Timeout, "AI service is temporarily unavailable.", retryAfter),
        _ => new ChatCompletionException(ChatCompletionErrorCode.Unknown, "AI request failed."),
    };

    private bool ShouldRetry(ChatCompletionErrorCode code, int attempt) =>
        attempt < _maxAttempts && code is ChatCompletionErrorCode.RateLimited or ChatCompletionErrorCode.Timeout;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken, TimeSpan? retryAfter = null) =>
        Task.Delay(retryAfter ?? TimeSpan.FromMilliseconds(Math.Min(100 * (1 << Math.Min(attempt - 1, 4)), 1600)), cancellationToken);
}
