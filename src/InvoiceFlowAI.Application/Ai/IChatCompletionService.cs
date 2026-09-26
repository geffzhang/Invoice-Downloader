// Abstraction over the DeepSeek (or any OpenAI-compatible) vision/text
// model. The service exposes two completion methods — text-only and
// vision (text + image bytes) — and raises ChatCompletionException with
// a stable error code so the caller can map 401/429/timeout/image-size
// to a candidate failure without leaking the raw exception text.

namespace InvoiceFlowAI.Application.Ai;

public interface IChatCompletionService
{
    Task<ChatCompletionResult> CompleteTextAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);

    Task<ChatCompletionResult> CompleteVisionAsync(
        ChatCompletionRequest request,
        IReadOnlyList<ChatImagePart> images,
        CancellationToken cancellationToken);
}

    public sealed record ChatImagePart(
        string ContentType,
        ReadOnlyMemory<byte> Bytes,
        int Width,
        int Height);

public sealed record ChatCompletionRequest(
    string SystemPrompt,
    string UserPrompt,
    string? Model = null,
    double? Temperature = null,
    int? MaxOutputTokens = null);

public sealed record ChatCompletionResult(
    string Text,
    string Model,
    int PromptTokens,
    int CompletionTokens);

public enum ChatCompletionErrorCode
{
    Unknown,
    Unauthorized,
    RateLimited,
    Timeout,
    ImageTooLarge,
    RequestTooLarge,
    UnsupportedImage,
    InvalidResponse,
}

public sealed class ChatCompletionException : Exception
{
    public ChatCompletionErrorCode Code { get; }
    public TimeSpan? RetryAfter { get; }
    public ChatCompletionException(ChatCompletionErrorCode code, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Code = code;
        RetryAfter = retryAfter;
    }
}
