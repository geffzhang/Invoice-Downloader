using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using UglyToad.PdfPig.Core;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class PdfInvoiceParser : IParser
{
    private readonly PdfTextExtractor _extractor;
    private readonly PdfInvoiceSegmenter _segmenter;
    private readonly PdfFieldParser _fieldParser;

    public PdfInvoiceParser() : this(new PdfTextExtractor(), new PdfInvoiceSegmenter(), new PdfFieldParser())
    {
    }

    public PdfInvoiceParser(PdfTextExtractor extractor, PdfInvoiceSegmenter segmenter, PdfFieldParser fieldParser)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _segmenter = segmenter ?? throw new ArgumentNullException(nameof(segmenter));
        _fieldParser = fieldParser ?? throw new ArgumentNullException(nameof(fieldParser));
    }

    public string ParserId => "pdf-text-invoice";
    public string Version => "1.0";
    public int Priority => 400;
    public IReadOnlyList<string> SourceKinds { get; } = ["pdf"];
    public string FailureCode => "PDF_INVALID";

    public bool CanParse(ParserWorkItem workItem) =>
        workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanParse(workItem)) return Task.FromResult(Failed("PDF_SOURCE_UNSUPPORTED", "Input is not a PDF source."));

        IReadOnlyList<PdfPageText> pages;
        try
        {
            pages = _extractor.ExtractPages(workItem.DocumentBytes, cancellationToken);
        }
        catch (PdfTextLimitException)
        {
            return Task.FromResult(Failed("PDF_TEXT_LIMIT_EXCEEDED", "PDF text or page count exceeds configured limits."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or PdfDocumentFormatException)
        {
            return Task.FromResult(Failed("PDF_INVALID", "PDF content is malformed or unsupported."));
        }

        if (pages.Count == 0 || pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
        {
            return Task.FromResult(NeedsFallback("PDF_TEXT_INSUFFICIENT", "PDF has no useful embedded text."));
        }

        var segments = _segmenter.Segment(pages);
        if (segments.Count == 0)
        {
            return Task.FromResult(NeedsFallback("PDF_TEXT_INSUFFICIENT", "PDF text does not contain an invoice marker."));
        }
        if (segments.Count > 1)
        {
            return Task.FromResult(NeedsFallback("PDF_MULTIPLE_INVOICES", "PDF contains multiple invoice segments."));
        }

        var segment = segments[0];
        var fields = _fieldParser.Parse(segment.Text);
        var invoice = new InvoiceDocument(
            DocumentId: workItem.Candidate.DocumentId.Value,
            InvoiceDate: fields.InvoiceDate,
            Purchaser: fields.Purchaser,
            Seller: fields.Seller,
            Amount: fields.Amount,
            TaxAmount: fields.TaxAmount,
            TotalAmount: fields.TotalAmount,
            InvoiceCode: fields.InvoiceCode,
            InvoiceNumber: fields.InvoiceNumber,
            DocumentType: fields.DocumentType,
            Category: string.Empty,
            Route: null,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: string.Empty)
        {
            Identity = workItem.Candidate.DocumentId,
            ParserName = ParserId,
            Confidence = fields.MissingFields.Count == 0 ? 0.9m : 0.55m,
        };

        if (fields.MissingFields.Count > 0)
        {
            var reason = fields.MissingFields.Contains("InvoiceNumber", StringComparer.Ordinal)
                ? "INVOICE_NUMBER_MISSING"
                : "PDF_FIELDS_INCOMPLETE";
            return Task.FromResult(new ParserOutcome(
                ParserId,
                Version,
                invoice,
                fields.MissingFields,
                new CandidateFailure(reason, FailureScope.Candidate, FailureCategory.Document, false,
                    "PDF invoice fields are incomplete."),
                ParserOutcomeDisposition.NeedsFallback));
        }

        return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), null));
    }

    private ParserOutcome Failed(string code, string message) => new(
        ParserId, Version, null, Array.Empty<string>(),
        new CandidateFailure(code, FailureScope.Candidate, FailureCategory.Document, false, message),
        ParserOutcomeDisposition.Failed);

    private ParserOutcome NeedsFallback(string code, string message) => new(
        ParserId, Version, null, Array.Empty<string>(),
        new CandidateFailure(code, FailureScope.Candidate, FailureCategory.Document, false, message),
        ParserOutcomeDisposition.NeedsFallback);
}