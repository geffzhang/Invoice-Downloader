using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Ocr;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ocr;

public sealed class PdfiumPageRendererTests
{
    [Fact]
    public async Task Render_caps_page_dimensions_and_image_bytes_and_preserves_page_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreatePdf(pageCount: 3));
        try
        {
            var renderer = new PdfiumPageRenderer();
            var pages = await renderer.RenderAsync(
                new DocumentSource(DocumentIdentity.Create("pdf-1"), LocalPath: path, MimeType: "application/pdf"),
                new PdfRenderOptions(MaximumPages: 2, MaximumWidth: 600, MaximumHeight: 600, MaximumImageBytes: 1_500_000),
                CancellationToken.None);

            pages.Should().HaveCount(2);
            pages.Select(page => page.PageNumber).Should().Equal(1, 2);
            pages.Should().OnlyContain(page => page.Width <= 600 && page.Height <= 600);
            pages.Should().OnlyContain(page => page.ImageBytes.Length > 0 && page.ImageBytes.Length <= 1_500_000);
            pages.Should().OnlyContain(page => page.MediaType == "image/png");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_rejects_non_pdf_sources_without_exposing_path()
    {
        const string secretPath = "private-path-sentinel";
        var renderer = new PdfiumPageRenderer();

        var act = () => renderer.RenderAsync(
            new DocumentSource(DocumentIdentity.Create("pdf-2"), LocalPath: secretPath, MimeType: "text/plain"),
            new PdfRenderOptions(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(act);
        exception.Message.Should().NotContain(secretPath);
    }

    [Fact]
    public async Task Render_rejects_corrupt_pdf_without_exposing_path()
    {
        const string secretPath = "private-corrupt-pdf-sentinel";
        var path = Path.Combine(Path.GetTempPath(), secretPath);
        await File.WriteAllTextAsync(path, "not a pdf");
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new PdfiumPageRenderer().RenderAsync(
                new DocumentSource(DocumentIdentity.Create("pdf-3"), LocalPath: path, MimeType: "application/pdf"),
                new PdfRenderOptions(), CancellationToken.None));

            exception.Message.Should().NotContain(secretPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_enforces_raw_image_byte_cap_before_allocating_bitmap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreatePdf(pageCount: 1));
        try
        {
            var act = () => new PdfiumPageRenderer().RenderAsync(
                new DocumentSource(DocumentIdentity.Create("pdf-4"), LocalPath: path, MimeType: "application/pdf"),
                new PdfRenderOptions(MaximumImageBytes: 100_000), CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(act);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreatePdf(int pageCount)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
        };
        var kids = new List<string>();
        for (var index = 0; index < pageCount; index++)
        {
            var pageObjectNumber = 3 + (index * 2);
            var contentObjectNumber = pageObjectNumber + 1;
            kids.Add($"{pageObjectNumber} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << >> /Contents {contentObjectNumber} 0 R >>");
            objects.Add("<< /Length 0 >>\nstream\n\nendstream");
        }
        objects.Insert(1, $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pageCount} >>");

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var (pdfObject, index) in objects.Select((value, index) => (value, index + 1)))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index).Append(" 0 obj\n").Append(pdfObject).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
