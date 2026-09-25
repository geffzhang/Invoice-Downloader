using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Extraction;

public sealed record InvoiceExtractionRules(
    string CompanyName,
    InvoiceAcceptancePolicy? AcceptancePolicy = null,
    int MinimumOcrTextCharacters = 40,
    string TrackAModel = "deepseek-flash",
    string TrackBModel = "deepseek-flash",
    int MaximumOutputTokens = 1200,
    PdfRenderOptions? RenderOptions = null);

public sealed record FieldExtractionRequest(
    DocumentCandidate Candidate,
    DocumentSource Source,
    string? EmbeddedText,
    InvoiceExtractionRules Rules,
    bool AllowVisionFallback,
    InvoiceResponseFields? DeterministicCorrections = null);

public sealed record FieldExtractionResult(
    DocumentIdentity Identity,
    InvoiceDocument? Document,
    AcceptanceDisposition Disposition,
    IReadOnlyList<InvoiceAcceptanceFailure> Failures,
    IReadOnlyList<string> Warnings,
    ExtractionRoute Route,
    string ReasonCode,
    ExtractionTrace Trace);

public interface IInvoiceFieldExtractor
{
    Task<FieldExtractionResult> ExtractAsync(FieldExtractionRequest request, CancellationToken cancellationToken);
}
