using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Ocr;
using SkiaSharp;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ocr;

public sealed class ImagePageRendererTests
{
    [Fact]
    public async Task Render_returns_single_png_page_for_png_source()
    {
        var path = WritePng(120, 80, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(SKRect.Create(10, 10, 40, 30), paint);
        });
        try
        {
            var pages = await new ImagePageRenderer().RenderAsync(
                new DocumentSource(DocumentIdentity.Create("png-1"), LocalPath: path, MimeType: "image/png"),
                new PdfRenderOptions(),
                CancellationToken.None);

            pages.Should().ContainSingle();
            pages[0].PageNumber.Should().Be(1);
            pages[0].Width.Should().Be(120);
            pages[0].Height.Should().Be(80);
            pages[0].MediaType.Should().Be("image/png");

            using var decoded = SKBitmap.Decode(pages[0].ImageBytes.ToArray());
            decoded.Should().NotBeNull();
            decoded!.Width.Should().Be(120);
            decoded.Height.Should().Be(80);
            decoded.GetPixel(20, 20).Should().Be(SKColors.Black);
            decoded.GetPixel(100, 60).Should().Be(SKColors.White);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_accepts_jpeg_source()
    {
        var path = WriteImage(64, 48, SKEncodedImageFormat.Jpeg);
        try
        {
            var pages = await new ImagePageRenderer().RenderAsync(
                new DocumentSource(DocumentIdentity.Create("jpeg-1"), LocalPath: path, MimeType: "image/jpeg"),
                new PdfRenderOptions(),
                CancellationToken.None);

            pages.Should().ContainSingle();
            pages[0].MediaType.Should().Be("image/png");
            pages[0].Width.Should().Be(64);
            pages[0].Height.Should().Be(48);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_downscales_to_dimension_and_byte_limits_preserving_aspect_ratio()
    {
        var path = WriteImage(1000, 500, SKEncodedImageFormat.Png);
        try
        {
            var pages = await new ImagePageRenderer().RenderAsync(
                new DocumentSource(DocumentIdentity.Create("large-1"), LocalPath: path, MimeType: "image/png"),
                new PdfRenderOptions(MaximumWidth: 200, MaximumHeight: 200, MaximumImageBytes: 1_000_000),
                CancellationToken.None);

            var page = pages.Should().ContainSingle().Subject;
            page.Width.Should().BeLessThanOrEqualTo(200);
            page.Height.Should().BeLessThanOrEqualTo(200);
            ((double)page.Width / page.Height).Should().BeApproximately(2.0, 0.02);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_downscales_to_raw_byte_limit()
    {
        var path = WriteImage(1000, 500, SKEncodedImageFormat.Png);
        try
        {
            var pages = await new ImagePageRenderer().RenderAsync(
                new DocumentSource(DocumentIdentity.Create("large-2"), LocalPath: path, MimeType: "image/png"),
                new PdfRenderOptions(MaximumWidth: 4096, MaximumHeight: 8192, MaximumImageBytes: 200_000),
                CancellationToken.None);

            var page = pages.Should().ContainSingle().Subject;
            ((long)page.Width * page.Height * 4).Should().BeLessThanOrEqualTo(210_000);
            ((double)page.Width / page.Height).Should().BeApproximately(2.0, 0.05);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_rejects_unsupported_mime_without_exposing_path()
    {
        const string secretPath = "private-image-path-sentinel";
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new ImagePageRenderer().RenderAsync(
            new DocumentSource(DocumentIdentity.Create("bad-mime"), LocalPath: secretPath, MimeType: "text/plain"),
            new PdfRenderOptions(), CancellationToken.None));

        exception.Message.Should().NotContain(secretPath);
    }

    [Fact]
    public async Task Render_rejects_corrupt_image_without_exposing_path()
    {
        const string secretPath = "private-corrupt-image-sentinel";
        var path = Path.Combine(Path.GetTempPath(), secretPath);
        await File.WriteAllTextAsync(path, "not an image", TestContext.Current.CancellationToken);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new ImagePageRenderer().RenderAsync(
                new DocumentSource(DocumentIdentity.Create("corrupt-image"), LocalPath: path, MimeType: "image/png"),
                new PdfRenderOptions(), CancellationToken.None));

            exception.Message.Should().NotContain(secretPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteImage(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(SKRect.Create(width / 4f, height / 4f, width / 2f, height / 3f), paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90)
            ?? throw new InvalidOperationException("Could not encode test image.");
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{Extension(format)}");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static string WritePng(int width, int height, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            draw(canvas);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode test image.");
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static string Extension(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => ".jpg",
        SKEncodedImageFormat.Png => ".png",
        _ => ".img",
    };
}
