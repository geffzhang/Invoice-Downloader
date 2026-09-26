namespace InvoiceFlowAI.Application.Extraction;

public interface IPdfPageRenderer
{
    Task<IReadOnlyList<RenderedPage>> RenderAsync(
        DocumentSource source,
        PdfRenderOptions options,
        CancellationToken cancellationToken);
}