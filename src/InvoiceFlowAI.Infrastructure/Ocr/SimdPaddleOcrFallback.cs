using System.Text.Json;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Application.Release;
using InvoiceFlowAI.Contracts.Release;
using InvoiceFlowAI.Contracts.Serialization;
using InvoiceFlowAI.Domain.Candidates;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using SkiaSharp;

namespace InvoiceFlowAI.Infrastructure.Ocr;

public sealed record SimdPaddleOcrOptions(
    string DetectionModelPath,
    string ClassificationModelPath,
    string RecognitionModelPath,
    string DictionaryPath,
    int MaximumConcurrency = 1,
    int MaximumPages = 2,
    int MaximumWidth = 4096,
    int MaximumHeight = 8192,
    long MaximumImageBytes = 10 * 1024 * 1024,
    bool UseEmbeddedModels = false,
    string? ModelManifestPath = null)
{
    public int EffectiveMaximumConcurrency => Math.Min(MaximumConcurrency, 2);

    public static SimdPaddleOcrOptions FromBaseDirectory(string baseDirectory) => new(
        Path.Combine(baseDirectory, "models", "ocr", "det.onnx"),
        Path.Combine(baseDirectory, "models", "ocr", "cls.onnx"),
        Path.Combine(baseDirectory, "models", "ocr", "rec.onnx"),
        Path.Combine(baseDirectory, "models", "ocr", "dict.txt"),
        UseEmbeddedModels: true,
        ModelManifestPath: Path.Combine(baseDirectory, "manifests", "model.json"));
}

public sealed class SimdPaddleOcrFallback : IOcrFallback, IAsyncDisposable
{
    private readonly SimdPaddleOcrOptions _options;
    private readonly SemaphoreSlim _concurrency;
    private PaddleOcrAll? _ocr;
    private bool _disposed;

    public SimdPaddleOcrFallback(SimdPaddleOcrOptions? options = null)
    {
        _options = options ?? SimdPaddleOcrOptions.FromBaseDirectory(AppContext.BaseDirectory);
        if (_options.MaximumConcurrency <= 0 || _options.MaximumPages <= 0
            || _options.MaximumWidth <= 0 || _options.MaximumHeight <= 0 || _options.MaximumImageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "OCR limits must be positive.");
        }

        _concurrency = new SemaphoreSlim(_options.EffectiveMaximumConcurrency, _options.EffectiveMaximumConcurrency);
    }

    public async Task<OcrFallbackOutcome> RecognizeAsync(
        DocumentIdentity identity,
        IReadOnlyList<RenderedPage> pages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pages);
        ValidatePages(pages);

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ocr = await GetOcrAsync(cancellationToken).ConfigureAwait(false);
            var lines = new List<OcrLine>();
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var decoded = SKBitmap.Decode(page.ImageBytes.ToArray())
                    ?? throw new InvalidDataException("OCR image could not be decoded.");
                if (decoded.Width != page.Width || decoded.Height != page.Height)
                {
                    throw new InvalidDataException("OCR image dimensions do not match the page metadata.");
                }

                using var normalized = decoded.ColorType == SKColorType.Bgra8888
                    ? decoded.Copy()
                    : decoded.Copy(SKColorType.Bgra8888);
                if (normalized is null || normalized.GetPixels() == IntPtr.Zero)
                {
                    throw new InvalidDataException("OCR image could not be prepared.");
                }

                var stride = checked(normalized.Width * 4);
                var pixels = new byte[checked(stride * normalized.Height)];
                for (var y = 0; y < normalized.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(normalized.GetPixels(), checked(y * normalized.RowBytes)),
                        pixels, checked(y * stride), stride);
                }

                var result = ocr.Run(pixels, normalized.Width, normalized.Height, stride, ImagePixelFormat.Bgra32);
                foreach (var line in result.Lines)
                {
                    lines.Add(ToOcrLine(line, page));
                }
            }

            var ordered = lines
                .OrderBy(line => line.PageNumber)
                .ThenBy(line => line.Bounds.Y)
                .ThenBy(line => line.Bounds.X)
                .ToArray();
            var confidence = ordered.Length == 0 ? 0m : ordered.Average(line => line.Confidence);
            return new OcrFallbackOutcome(ordered, confidence);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("OCR recognition failed.");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _concurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _ocr?.Dispose();
            _ocr = null;
        }
        finally
        {
            _concurrency.Release();
            _concurrency.Dispose();
        }
    }

    private async Task<PaddleOcrAll> GetOcrAsync(CancellationToken cancellationToken)
    {
        if (_ocr is not null)
        {
            return _ocr;
        }

        if (!_options.UseEmbeddedModels && new[]
            {
                _options.DetectionModelPath,
                _options.ClassificationModelPath,
                _options.RecognitionModelPath,
                _options.DictionaryPath,
            }.Any(path => string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
        {
            throw new InvalidOperationException("OCR model assets are unavailable.");
        }

        if (_options.UseEmbeddedModels)
        {
            VerifyEmbeddedModelManifest();
        }

        try
        {
            _ocr = _options.UseEmbeddedModels
                ? await PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default, new PaddleOcrOptions(), cancellationToken).ConfigureAwait(false)
                : await PaddleOcrAll.LoadAsync(
                    _options.DetectionModelPath,
                    _options.ClassificationModelPath,
                    _options.RecognitionModelPath,
                    _options.DictionaryPath,
                    new PaddleOcrOptions(),
                    cancellationToken).ConfigureAwait(false);
            return _ocr;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("OCR model initialization failed.");
        }
    }

    private void VerifyEmbeddedModelManifest()
    {
        try
        {
            var manifestPath = _options.ModelManifestPath;
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                throw new InvalidDataException();
            }

            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ModelManifest>(manifestJson, InvoiceJsonOptions.Strict);
            var publishRoot = Path.GetDirectoryName(Path.GetDirectoryName(manifestPath)!)!;
            var modelRoot = Path.Combine(publishRoot, "models");
            var report = new ReleaseManifestVerifier().VerifyModel(manifest, modelRoot);
            if (!report.AllPresent)
            {
                throw new InvalidDataException();
            }
        }
        catch
        {
            throw new InvalidOperationException("OCR model manifest is unavailable or invalid.");
        }
    }

    private void ValidatePages(IReadOnlyList<RenderedPage> pages)
    {
        if (pages.Count == 0 || pages.Count > Math.Min(_options.MaximumPages, 2))
        {
            throw new InvalidDataException("OCR page count is outside the configured limit.");
        }

        foreach (var page in pages)
        {
            if (page.PageNumber <= 0 || page.Width <= 0 || page.Height <= 0
                || page.Width > Math.Min(_options.MaximumWidth, 4096)
                || page.Height > Math.Min(_options.MaximumHeight, 8192)
                || page.ImageBytes.Length == 0 || page.ImageBytes.Length > _options.MaximumImageBytes
                || page.MediaType is not ("image/png" or "image/jpeg"))
            {
                throw new InvalidDataException("OCR page metadata or image is invalid.");
            }

            if (checked((long)page.Width * page.Height * 4) > _options.MaximumImageBytes)
            {
                throw new InvalidDataException("OCR image exceeds the configured byte limit.");
            }
        }
    }

    private static OcrLine ToOcrLine(PaddleOcrLine line, RenderedPage page)
    {
        var box = line.Box;
        var left = Math.Clamp(Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4)), 0, page.Width);
        var top = Math.Clamp(Math.Min(Math.Min(box.Y1, box.Y2), Math.Min(box.Y3, box.Y4)), 0, page.Height);
        var right = Math.Clamp(Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4)), left, page.Width);
        var bottom = Math.Clamp(Math.Max(Math.Max(box.Y1, box.Y2), Math.Max(box.Y3, box.Y4)), top, page.Height);
        var confidence = decimal.Clamp((decimal)line.RecognitionScore, 0m, 1m);
        return new OcrLine(line.Text, confidence, new BoundingBox(left, top, right - left, bottom - top), page.PageNumber);
    }
}