// Verifies DeepSeekChatCompletionService image-size enforcement (Task 8).
// The 401 / 429 / timeout error codes are exercised in the
// EmailBodyReceiptService tests via the fake chat service — the real
// mapping from Microsoft.Extensions.AI exceptions lives in the wiring
// registered in Task 11 and is covered by integration tests there.

using FluentAssertions;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Infrastructure.Ai;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ai;

public sealed class DeepSeekChatCompletionServiceTests
{
    [Fact]
    public async Task Vision_request_rejects_oversized_image()
    {
        var service = new DeepSeekChatCompletionService(maxImageBytes: 1024);

        var act = () => service.CompleteVisionAsync(
            new ChatCompletionRequest("sys", "user"),
            new byte[2048],
            "image/png",
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ChatCompletionException>();
        ex.Which.Code.Should().Be(ChatCompletionErrorCode.ImageTooLarge);
    }

    [Fact]
    public async Task Vision_request_within_limit_proceeds_to_text_completion()
    {
        // The text path on the real service is wired in Task 11; here we
        // assert that the size guard does not fire and that the call
        // throws NotImplementedException (i.e. it reached the unwired
        // text-completion body). The behaviour we care about for Task 8
        // is the size guard; the production wiring is verified in
        // Task 11's DI-composition test.
        var service = new DeepSeekChatCompletionService(maxImageBytes: 1024);

        var act = () => service.CompleteVisionAsync(
            new ChatCompletionRequest("sys", "user"),
            new byte[256],
            "image/png",
            CancellationToken.None);

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public void Default_max_image_bytes_is_4MiB()
    {
        DeepSeekChatCompletionService.DefaultMaxImageBytes.Should().Be(4 * 1024 * 1024);
    }
}
