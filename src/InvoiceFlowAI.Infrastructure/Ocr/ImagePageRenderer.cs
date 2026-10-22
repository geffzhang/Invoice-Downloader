using InvoiceFlowAI.Application.Extraction;
using SkiaSharp;

namespace InvoiceFlowAI.Infrastructure.Ocr;

public sealed class ImagePageRenderer : IPdfPageRenderer
{
    private const int MaximumWidth = 4096;
    private const int MaximumHeight = 8192;
    private const long MaximumSourcePixels = 40_000_000;

    private static readonly HashSet<string> SupportedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/gif",
        "image/bmp",
        "image/webp",
    };

    public static bool IsSupportedMimeType(string? mimeType) =>
        SupportedMimeTypes.Contains(NormalizeMimeType(mimeType));

    public Task<IReadOnlyList<RenderedPage>> RenderAsync(
        DocumentSource source,
        PdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupportedMimeType(source.MimeType)
            || string.IsNullOrWhiteSpace(source.LocalPath)
            || !File.Exists(source.LocalPath))
        {
            throw new InvalidDataException("The document is not an available image source.");
        }

        if (options.MaximumWidth <= 0 || options.MaximumHeight <= 0 || options.MaximumImageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image render limits must be positive.");
        }

        return Task.FromResult(RenderCore(source.LocalPath, options, cancellationToken));
    }

    private static IReadOnlyList<RenderedPage> RenderCore(
        string path,
        PdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidDataException("The image could not be decoded.");
        var sourceInfo = codec.Info;
        if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0)
        {
            throw new InvalidDataException("The image has invalid dimensions.");
        }

        var origin = codec.EncodedOrigin;
        var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var orientedWidth = sideways ? sourceInfo.Height : sourceInfo.Width;
        var orientedHeight = sideways ? sourceInfo.Width : sourceInfo.Height;

        var maximumWidth = Math.Min(options.MaximumWidth, MaximumWidth);
        var maximumHeight = Math.Min(options.MaximumHeight, MaximumHeight);
        var scale = Math.Min(
            1d,
            Math.Min(
                (double)maximumWidth / orientedWidth,
                (double)maximumHeight / orientedHeight));
        scale = Math.Min(
            scale,
            Math.Sqrt(options.MaximumImageBytes / 4d / ((double)orientedWidth * orientedHeight)));

        var targetWidth = Math.Max(1, (int)Math.Floor(orientedWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Floor(orientedHeight * scale));
        var decodeWidth = sideways ? targetHeight : targetWidth;
        var decodeHeight = sideways ? targetWidth : targetHeight;

        using var decoded = DecodeSource(codec, path, decodeWidth, decodeHeight, sourceInfo);
        using var fitted = FitWithinRawByteLimit(decoded, options.MaximumImageBytes);
        cancellationToken.ThrowIfCancellationRequested();

        using var oriented = ImageOrientation.Apply(fitted, origin);
        var bytes = SkiaImagePreprocessor.EncodePng(
            oriented.GetPixels(),
            oriented.Width,
            oriented.Height,
            oriented.RowBytes,
            options.MaximumImageBytes);
        return [new RenderedPage(1, oriented.Width, oriented.Height, "image/png", bytes)];
    }

    private static SKBitmap DecodeSource(
        SKCodec codec,
        string path,
        int width,
        int height,
        SKImageInfo sourceInfo)
    {
        var fullDecodeAllowed = (long)sourceInfo.Width * sourceInfo.Height <= MaximumSourcePixels;
        var scale = Math.Min((double)width / sourceInfo.Width, (double)height / sourceInfo.Height);
        if (scale < 1d)
        {
            var scaled = codec.GetScaledDimensions((float)scale);
            if (scaled.Width > 0 && scaled.Height > 0
                && scaled.Width <= sourceInfo.Width && scaled.Height <= sourceInfo.Height
                && (long)scaled.Width * scaled.Height <= MaximumSourcePixels)
            {
                var bitmap = TryDecodeAtCodecSize(codec, scaled);
                if (bitmap is not null)
                {
                    using (bitmap)
                    {
                        return DecodeAtSize(bitmap, width, height);
                    }
                }
            }
        }

        if (!fullDecodeAllowed)
        {
            throw new InvalidDataException("The image is too large to decode.");
        }

        using var native = SKBitmap.Decode(path)
            ?? throw new InvalidDataException("The image could not be decoded.");
        return DecodeAtSize(native, width, height);
    }

    private static SKBitmap? TryDecodeAtCodecSize(SKCodec codec, SKSizeI size)
    {
        var targetInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(targetInfo);
        if (bitmap.GetPixels() == IntPtr.Zero)
        {
            bitmap.Dispose();
            return null;
        }

        try
        {
            if (codec.GetPixels(targetInfo, bitmap.GetPixels()) == SKCodecResult.Success)
            {
                return bitmap;
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        bitmap.Dispose();
        return null;
    }

    private static SKBitmap DecodeAtSize(SKBitmap source, int width, int height)
    {
        if (source.Width == width && source.Height == height
            && source.ColorType == SKColorType.Bgra8888
            && source.AlphaType == SKAlphaType.Premul)
        {
            return source.Copy() ?? throw new InvalidDataException("The image could not be prepared.");
        }

        return source.Resize(
                   new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                   new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
               ?? throw new InvalidDataException("The image could not be resized.");
    }

    private static SKBitmap FitWithinRawByteLimit(SKBitmap bitmap, long maximumBytes)
    {
        var pixels = (long)bitmap.Width * bitmap.Height;
        if (pixels * 4 <= maximumBytes)
        {
            return bitmap.Copy() ?? throw new InvalidDataException("The image could not be prepared.");
        }

        var factor = Math.Sqrt(maximumBytes / 4d / pixels) * 0.999;
        var width = Math.Max(1, (int)Math.Floor(bitmap.Width * factor));
        var height = Math.Max(1, (int)Math.Floor(bitmap.Height * factor));
        return bitmap.Resize(
                   new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                   new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
               ?? throw new InvalidDataException("The image could not be resized.");
    }

    private static string NormalizeMimeType(string? mimeType) =>
        (mimeType ?? string.Empty).Split(';')[0].Trim();
}
