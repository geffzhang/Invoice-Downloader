using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Contracts.Release;
using InvoiceFlowAI.Contracts.Serialization;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Ocr;
using System.Security.Cryptography;
using System.Text.Json;
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
    public void Production_default_uses_the_embedded_chinese_v6_tiny_model_bundle()
    {
        var options = SimdPaddleOcrOptions.FromBaseDirectory(AppContext.BaseDirectory);

        options.UseEmbeddedModels.Should().BeTrue();
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

    [Fact]
    public async Task Recognize_rejects_missing_embedded_model_manifest_with_stable_error()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoiceflow-model-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var fallback = new SimdPaddleOcrFallback(new SimdPaddleOcrOptions(
            "det", "cls", "rec", "dict", UseEmbeddedModels: true,
            ModelManifestPath: Path.Combine(root, "missing-model.json")));
        var page = new RenderedPage(1, 1, 1, "image/png", new byte[] { 137, 80, 78, 71 });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fallback.RecognizeAsync(
            DocumentIdentity.Create("ocr-manifest"), [page], CancellationToken.None));

        exception.Message.Should().Be("OCR model manifest is unavailable or invalid.");
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Embedded_chinese_v6_tiny_bundle_initializes_offline_before_image_decode()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoiceflow-model-smoke-{Guid.NewGuid():N}");
        var modelRoot = Path.Combine(root, "models");
        var modelDirectory = Path.Combine(modelRoot, "ocr");
        var manifestDirectory = Path.Combine(root, "manifests");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(manifestDirectory);
        try
        {
            var packageAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .Single(assembly => assembly.GetName().Name == "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny");
            const string relativePath = "ocr/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll";
            var stagedAssembly = Path.Combine(modelRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Copy(packageAssembly.Location, stagedAssembly);
            var bytes = await File.ReadAllBytesAsync(stagedAssembly);
            var asset = new ModelManifestAsset(relativePath, bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "model-bundle", "1.0.0", null);
            var manifest = new ModelManifest(1, "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny", [asset], "fixture");
            var manifestPath = Path.Combine(manifestDirectory, "model.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, InvoiceJsonOptions.Strict));
            await using var fallback = new SimdPaddleOcrFallback(new SimdPaddleOcrOptions(
                "det", "cls", "rec", "dict", UseEmbeddedModels: true, ModelManifestPath: manifestPath));

            var act = () => fallback.RecognizeAsync(
                DocumentIdentity.Create("offline-model-smoke"),
                [new RenderedPage(1, 1, 1, "image/png", new byte[] { 137, 80, 78, 71 })],
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(act);

            exception.Message.Should().Be("OCR recognition failed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
