using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Ocr;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ocr;

public sealed class SimdPaddleOcrFallbackTests
{
    [Fact]
    public void Maximum_concurrency_is_capped_at_two()
    {
        var options = new SimdPaddleOcrOptions("det", "cls", "rec", "dict", MaximumConcurrency: 8);

        options.EffectiveMaximumConcurrency.Should().Be(2);
    }

    [Fact]
    public async Task Recognize_rejects_invalid_image_before_model_initialization()
    {
        var fallback = new SimdPaddleOcrFallback(new SimdPaddleOcrOptions(
            Path.Combine(Path.GetTempPath(), "missing-det.onnx"),
            Path.Combine(Path.GetTempPath(), "missing-cls.onnx"),
            Path.Combine(Path.GetTempPath(), "missing-rec.onnx"),
            Path.Combine(Path.GetTempPath(), "missing-dict.txt")));

        var act = async () => await fallback.RecognizeAsync(
            DocumentIdentity.Create("ocr-1"),
            [new RenderedPage(1, 1, 1, "text/plain", new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }))],
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(act);
    }

    [Fact]
    public async Task Recognize_propagates_cancellation_before_model_initialization()
    {
        var fallback = new SimdPaddleOcrFallback(new SimdPaddleOcrOptions(
            "det.onnx", "cls.onnx", "rec.onnx", "dict.txt"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await fallback.RecognizeAsync(
            DocumentIdentity.Create("ocr-2"), Array.Empty<RenderedPage>(), cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    [Fact]
    public async Task Recognize_reports_missing_local_models_without_exposing_model_paths()
    {
        const string modelSentinel = "private-model-path-sentinel";
        var fallback = new SimdPaddleOcrFallback(new SimdPaddleOcrOptions(
            Path.Combine(Path.GetTempPath(), modelSentinel, "det.onnx"),
            Path.Combine(Path.GetTempPath(), modelSentinel, "cls.onnx"),
            Path.Combine(Path.GetTempPath(), modelSentinel, "rec.onnx"),
            Path.Combine(Path.GetTempPath(), modelSentinel, "dict.txt")));
        var page = new RenderedPage(1, 1, 1, "image/png", new ReadOnlyMemory<byte>(new byte[] { 137, 80, 78, 71 }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await fallback.RecognizeAsync(
            DocumentIdentity.Create("ocr-3"), [page], CancellationToken.None));

        exception.Message.Should().NotContain(modelSentinel);
    }
}
