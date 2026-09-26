using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using UglyToad.PdfPig.Core;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class DidiInvoiceParser : IParser
{
    private readonly PdfTextExtractor _extractor;
    private readonly PdfFieldParser _fieldParser;

    public DidiInvoiceParser() : this(new PdfTextExtractor(), new PdfFieldParser())
    {
    }

    public DidiInvoiceParser(PdfTextExtractor extractor, PdfFieldParser fieldParser)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _fieldParser = fieldParser ?? throw new ArgumentNullException(nameof(fieldParser));
    }

    public string ParserId => "didi-invoice";
    public string Version => "1.0";
    public int Priority => 460;
    public IReadOnlyList<string> SourceKinds { get; } = ["pdf"];
    public string FailureCode => "DIDI_PARSER_FAILED";

    public bool CanParse(ParserWorkItem workItem)
    {
        if (!workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return IsDidiInvoice(string.Join("\n", _extractor.ExtractPages(workItem.DocumentBytes, CancellationToken.None).Select(page => page.Text)));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or PdfDocumentFormatException)
        {
            return false;
        }
    }

    public Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Failed("PDF_SOURCE_UNSUPPORTED", "Input is not a PDF source."));

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

        var text = string.Join("\n", pages.Select(page => page.Text));
        if (!IsDidiInvoice(text))
            return Task.FromResult(NeedsFallback("DIDI_MARKER_NOT_FOUND", "PDF does not contain a Didi transport invoice marker."));

        var fields = _fieldParser.Parse(text);
        var total = ReadAmount(text, "Total Amount", "Price Tax Total", "价税合计") ?? fields.TotalAmount;
        var amount = total ?? fields.Amount;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(fields.InvoiceNumber)) missing.Add("InvoiceNumber");
        if (fields.InvoiceDate is null) missing.Add("InvoiceDate");
        if (amount is null) missing.Add("Amount");
        var seller = string.IsNullOrWhiteSpace(fields.Seller) ? "滴滴出行" : fields.Seller;
        var invoice = new InvoiceDocument(
            DocumentId: workItem.Candidate.DocumentId.Value,
            InvoiceDate: fields.InvoiceDate,
            Purchaser: fields.Purchaser,
            Seller: seller,
            Amount: amount,
            TaxAmount: fields.TaxAmount,
            TotalAmount: total,
            InvoiceCode: fields.InvoiceCode,
            InvoiceNumber: fields.InvoiceNumber,
            DocumentType: InvoiceDocumentType.RideInvoice,
            Category: "Ride",
            Route: null,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: string.Empty)
        {
            Identity = workItem.Candidate.DocumentId,
            ParserName = ParserId,
            Confidence = missing.Count == 0 ? 0.92m : 0.55m,
        };

        if (missing.Count > 0)
            return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, missing,
                new CandidateFailure("DIDI_FIELDS_INCOMPLETE", FailureScope.Candidate, FailureCategory.Document, false,
                    "Didi invoice fields are incomplete."), ParserOutcomeDisposition.NeedsFallback));

        return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), null));
    }

    private static bool IsDidiInvoice(string text)
    {
        var hasProvider = text.Contains("滴滴出行", StringComparison.Ordinal)
            || text.Contains("didi", StringComparison.OrdinalIgnoreCase);
        var hasTransportEvidence = text.Contains("运输服务", StringComparison.Ordinal)
            || text.Contains("Passenger Transport", StringComparison.OrdinalIgnoreCase)
            || text.Contains("行程单", StringComparison.Ordinal);
        return hasProvider && hasTransportEvidence;
    }

    private static decimal? ReadAmount(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text,
                $@"(?im)^\s*{System.Text.RegularExpressions.Regex.Escape(label)}\s*[:：]?\s*(?<amount>[-(]?[¥￥]?[0-9,]+(?:\.[0-9]{{1,2}})?\)?)\s*$");
            if (match.Success)
            {
                var value = match.Groups["amount"].Value.Replace(",", string.Empty, StringComparison.Ordinal)
                    .Replace("¥", string.Empty, StringComparison.Ordinal).Replace("￥", string.Empty, StringComparison.Ordinal);
                if (value.StartsWith('(') && value.EndsWith(')')) value = "-" + value[1..^1];
                if (decimal.TryParse(value, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var amount))
                    return amount;
            }
        }
        return null;
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