using InvoiceFlowAI.Application.Extraction;

namespace InvoiceFlowAI.Infrastructure.Ocr;

public sealed class DocumentPageRenderer : IPdfPageRenderer
{
    private readonly IPdfPageRenderer _pdfRenderer;
    private readonly IPdfPageRenderer _imageRenderer;

    public DocumentPageRenderer(IPdfPageRenderer pdfRenderer, IPdfPageRenderer imageRenderer)
    {
        _pdfRenderer = pdfRenderer ?? throw new ArgumentNullException(nameof(pdfRenderer));
        _imageRenderer = imageRenderer ?? throw new ArgumentNullException(nameof(imageRenderer));
    }

    public Task<IReadOnlyList<RenderedPage>> RenderAsync(
        DocumentSource source,
        PdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var mimeType = NormalizeMimeType(source.MimeType);
        if (mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return _pdfRenderer.RenderAsync(source, options, cancellationToken);
        }

        if (ImagePageRenderer.IsSupportedMimeType(mimeType))
        {
            return _imageRenderer.RenderAsync(source, options, cancellationToken);
        }

        throw new InvalidDataException("The document is not a supported page source.");
    }

    private static string NormalizeMimeType(string? mimeType) =>
        (mimeType ?? string.Empty).Split(';')[0].Trim();
}
