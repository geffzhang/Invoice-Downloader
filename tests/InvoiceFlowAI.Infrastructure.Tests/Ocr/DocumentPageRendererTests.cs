using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Ocr;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Ocr;

public sealed class DocumentPageRendererTests
{
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/pdf; name=invoice.pdf")]
    [InlineData("IMAGE/PNG")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/bmp")]
    [InlineData("image/webp")]
    public async Task Render_dispatches_supported_mime_types(string mimeType)
    {
        var pdf = new RecordingRenderer();
        var image = new RecordingRenderer();
        var renderer = new DocumentPageRenderer(pdf, image);

        await renderer.RenderAsync(
            new DocumentSource(DocumentIdentity.Create("dispatch"), LocalPath: "ignored", MimeType: mimeType),
            new PdfRenderOptions(),
            CancellationToken.None);

        var expected = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? image : pdf;
        var other = expected == image ? pdf : image;
        expected.CallCount.Should().Be(1);
        other.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Render_rejects_unsupported_mime_without_calling_children()
    {
        var pdf = new RecordingRenderer();
        var image = new RecordingRenderer();
        var renderer = new DocumentPageRenderer(pdf, image);

        var act = () => renderer.RenderAsync(
            new DocumentSource(DocumentIdentity.Create("unsupported"), LocalPath: "ignored", MimeType: "text/plain"),
            new PdfRenderOptions(),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(act);
        pdf.CallCount.Should().Be(0);
        image.CallCount.Should().Be(0);
    }

    private sealed class RecordingRenderer : IPdfPageRenderer
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<RenderedPage>> RenderAsync(
            DocumentSource source,
            PdfRenderOptions options,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<RenderedPage>>(
                [new RenderedPage(1, 1, 1, "image/png", new byte[] { 1 })]);
        }
    }
}
