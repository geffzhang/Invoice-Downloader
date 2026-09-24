using SkiaSharp;

namespace InvoiceFlowAI.Infrastructure.Ocr;

internal static class SkiaImagePreprocessor
{
    public static byte[] EncodePng(IntPtr pixels, int width, int height, int stride, long maximumBytes)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var destination = bitmap.GetPixels();
        if (destination == IntPtr.Zero)
        {
            throw new InvalidDataException("Rendered image could not be prepared.");
        }

        var rowBytes = checked(width * 4);
        var row = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(pixels, checked(y * stride)), row, 0, rowBytes);
            System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(destination, checked(y * bitmap.RowBytes)), rowBytes);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Rendered image could not be encoded.");
        if (encoded.Size > maximumBytes)
        {
            throw new InvalidDataException("Rendered image exceeds the configured byte limit.");
        }

        return encoded.ToArray();
    }
}