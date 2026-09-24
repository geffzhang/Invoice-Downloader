using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Extraction;

public sealed record OcrFallbackOutcome(
    IReadOnlyList<OcrLine> Lines,
    decimal AggregateConfidence);

public interface IOcrFallback
{
    Task<OcrFallbackOutcome> RecognizeAsync(
        DocumentIdentity identity,
        IReadOnlyList<RenderedPage> pages,
        CancellationToken cancellationToken);
}