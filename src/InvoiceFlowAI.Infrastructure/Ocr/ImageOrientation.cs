using System.Runtime.InteropServices;
using SkiaSharp;

namespace InvoiceFlowAI.Infrastructure.Ocr;

internal static class ImageOrientation
{
    public static SKBitmap Apply(SKBitmap source, SKEncodedOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.ColorType != SKColorType.Bgra8888)
        {
            throw new ArgumentException("The source bitmap must use BGRA8888 pixels.", nameof(source));
        }

        if (origin == SKEncodedOrigin.TopLeft)
        {
            return source.Copy() ?? throw new InvalidDataException("The image could not be copied.");
        }

        var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = sideways ? source.Height : source.Width;
        var height = sideways ? source.Width : source.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (result.GetPixels() == IntPtr.Zero)
        {
            result.Dispose();
            throw new InvalidDataException("The image could not be prepared.");
        }

        try
        {
            var sourcePixels = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
            var targetPixels = MemoryMarshal.Cast<byte, uint>(result.GetPixelSpan());
            var sourceStride = source.RowBytes / 4;
            var targetStride = result.RowBytes / 4;
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var (targetX, targetY) = Map(x, y, source.Width, source.Height, origin);
                    targetPixels[(targetY * targetStride) + targetX] = sourcePixels[(y * sourceStride) + x];
                }
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static (int X, int Y) Map(int x, int y, int width, int height, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopLeft => (x, y),
        SKEncodedOrigin.TopRight => (width - 1 - x, y),
        SKEncodedOrigin.BottomRight => (width - 1 - x, height - 1 - y),
        SKEncodedOrigin.BottomLeft => (x, height - 1 - y),
        SKEncodedOrigin.LeftTop => (y, x),
        SKEncodedOrigin.RightTop => (height - 1 - y, x),
        SKEncodedOrigin.RightBottom => (height - 1 - y, width - 1 - x),
        SKEncodedOrigin.LeftBottom => (y, width - 1 - x),
        _ => (x, y),
    };
}
