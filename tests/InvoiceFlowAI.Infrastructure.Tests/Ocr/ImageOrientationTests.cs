using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Ocr;
using SkiaSharp;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ocr;

public sealed class ImageOrientationTests
{
    private static readonly SKColor A = new(255, 0, 0);
    private static readonly SKColor B = new(0, 255, 0);
    private static readonly SKColor C = new(0, 0, 255);
    private static readonly SKColor D = new(255, 255, 0);

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, new[] { "A", "B", "C", "D" }, 2, 2)]
    [InlineData(SKEncodedOrigin.TopRight, new[] { "B", "A", "D", "C" }, 2, 2)]
    [InlineData(SKEncodedOrigin.BottomRight, new[] { "D", "C", "B", "A" }, 2, 2)]
    [InlineData(SKEncodedOrigin.BottomLeft, new[] { "C", "D", "A", "B" }, 2, 2)]
    [InlineData(SKEncodedOrigin.LeftTop, new[] { "A", "C", "B", "D" }, 2, 2)]
    [InlineData(SKEncodedOrigin.RightTop, new[] { "C", "A", "D", "B" }, 2, 2)]
    [InlineData(SKEncodedOrigin.RightBottom, new[] { "D", "B", "C", "A" }, 2, 2)]
    [InlineData(SKEncodedOrigin.LeftBottom, new[] { "B", "D", "A", "C" }, 2, 2)]
    public void Apply_reorients_pixels(
        SKEncodedOrigin origin,
        string[] expected,
        int expectedWidth,
        int expectedHeight)
    {
        using var source = CreateSource();

        using var result = ImageOrientation.Apply(source, origin);

        result.Width.Should().Be(expectedWidth);
        result.Height.Should().Be(expectedHeight);
        Read(result).Should().Equal(expected);
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft)]
    [InlineData(SKEncodedOrigin.TopRight)]
    [InlineData(SKEncodedOrigin.BottomRight)]
    [InlineData(SKEncodedOrigin.BottomLeft)]
    public void Apply_keeps_dimensions_for_upright_origins(SKEncodedOrigin origin)
    {
        using var source = new SKBitmap(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var result = ImageOrientation.Apply(source, origin);

        result.Width.Should().Be(3);
        result.Height.Should().Be(2);
    }

    [Theory]
    [InlineData(SKEncodedOrigin.LeftTop)]
    [InlineData(SKEncodedOrigin.RightTop)]
    [InlineData(SKEncodedOrigin.RightBottom)]
    [InlineData(SKEncodedOrigin.LeftBottom)]
    public void Apply_swaps_dimensions_for_sideways_origins(SKEncodedOrigin origin)
    {
        using var source = new SKBitmap(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var result = ImageOrientation.Apply(source, origin);

        result.Width.Should().Be(2);
        result.Height.Should().Be(3);
    }

    private static SKBitmap CreateSource()
    {
        var bitmap = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.SetPixel(0, 0, A);
        bitmap.SetPixel(1, 0, B);
        bitmap.SetPixel(0, 1, C);
        bitmap.SetPixel(1, 1, D);
        return bitmap;
    }

    private static string[] Read(SKBitmap bitmap)
    {
        var colors = new Dictionary<SKColor, string>
        {
            [A] = "A",
            [B] = "B",
            [C] = "C",
            [D] = "D",
        };
        var result = new string[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                result[y * bitmap.Width + x] = colors[bitmap.GetPixel(x, y)];
            }
        }

        return result;
    }
}
