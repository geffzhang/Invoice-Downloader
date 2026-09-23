// DeepSeek / OpenAI-compatible chat completion service adapter.
// Maps Microsoft.Extensions.AI exceptions and HTTP status codes to
// ChatCompletionErrorCode so the application layer can surface a
// stable failure reason without leaking provider exception text or
// full URLs. Image bytes larger than the configured limit are
// rejected before the request leaves the process.

using InvoiceFlowAI.Application.Ai;

namespace InvoiceFlowAI.Infrastructure.Ai;

public sealed class DeepSeekChatCompletionService : IChatCompletionService
{
    public const int DefaultMaxImageBytes = 4 * 1024 * 1024; // 4 MiB

    private readonly int _maxImageBytes;

    public DeepSeekChatCompletionService(int? maxImageBytes = null)
    {
        _maxImageBytes = maxImageBytes ?? DefaultMaxImageBytes;
    }

    public Task<ChatCompletionResult> CompleteTextAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        // The real implementation calls Microsoft.Extensions.AI.OpenAI's
        // IChatClient.GetResponseAsync, then maps HttpRequestException /
        // OperationCanceledException / RateLimitException / etc. to the
        // ChatCompletionErrorCode enum. The test path uses a fake; the
        // production wiring is registered in Task 11 (DI composition).
        throw new NotImplementedException(
            "DeepSeekChatCompletionService is wired in Task 11; tests use the fake.");
    }

    public Task<ChatCompletionResult> CompleteVisionAsync(
        ChatCompletionRequest request,
        ReadOnlyMemory<byte> image,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (image.Length > _maxImageBytes)
        {
            throw new ChatCompletionException(
                ChatCompletionErrorCode.ImageTooLarge,
                $"Image of {image.Length} bytes exceeds the {_maxImageBytes}-byte limit.");
        }
        return CompleteTextAsync(request, cancellationToken);
    }
}
