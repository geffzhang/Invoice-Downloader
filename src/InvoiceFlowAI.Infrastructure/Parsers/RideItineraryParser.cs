using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using UglyToad.PdfPig.Core;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class RideItineraryParser : IParser
{
    private readonly PdfTextExtractor _extractor;

    public RideItineraryParser() : this(new PdfTextExtractor(new PdfTextExtractionOptions(MaximumPages: 2)))
    {
    }

    public RideItineraryParser(PdfTextExtractor extractor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public string ParserId => "ride-itinerary";
    public string Version => "1.0";
    public int Priority => 470;
    public IReadOnlyList<string> SourceKinds { get; } = ["pdf"];
    public string FailureCode => "RIDE_ITINERARY_PARSER_FAILED";

    public bool CanParse(ParserWorkItem workItem)
    {
        if (!workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return IsRideItinerary(string.Join("\n", _extractor.ExtractPages(workItem.DocumentBytes, CancellationToken.None).Select(page => page.Text)));
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

        var text = string.Join("\n", pages.Select(page => page.Text)).Replace('\u00a0', ' ');
        if (!IsRideItinerary(text))
            return Task.FromResult(NeedsFallback("RIDE_ITINERARY_MARKER_NOT_FOUND", "PDF does not contain a supported ride itinerary marker."));

        var isAmap = text.Contains("高德", StringComparison.Ordinal) || text.Contains("AMAP", StringComparison.OrdinalIgnoreCase);
        var date = ParseTripStartDate(text);
        var amount = ParseTotalAmount(text);
        var missing = new List<string>();
        if (date is null) missing.Add("InvoiceDate");
        if (amount is null) missing.Add("Amount");
        var invoice = new InvoiceDocument(
            DocumentId: workItem.Candidate.DocumentId.Value,
            InvoiceDate: date,
            Purchaser: "个人",
            Seller: isAmap ? "高德地图" : "滴滴出行",
            Amount: amount,
            TaxAmount: null,
            TotalAmount: amount,
            InvoiceCode: null,
            InvoiceNumber: null,
            DocumentType: InvoiceDocumentType.RideItinerary,
            Category: "Ride",
            Route: null,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: string.Empty)
        {
            Identity = workItem.Candidate.DocumentId,
            IsInvoice = false,
            ParserName = ParserId,
            Flags = InvoiceFlags.Itinerary,
            Confidence = missing.Count == 0 ? 0.88m : 0.5m,
        };

        if (missing.Count > 0)
            return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, missing,
                new CandidateFailure("RIDE_ITINERARY_FIELDS_INCOMPLETE", FailureScope.Candidate, FailureCategory.Document, false,
                    "Ride itinerary fields are incomplete."), ParserOutcomeDisposition.NeedsFallback));

        return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), null));
    }

    private static bool IsRideItinerary(string text)
    {
        var hasPlatform = text.Contains("高德", StringComparison.Ordinal)
            || text.Contains("AMAP", StringComparison.OrdinalIgnoreCase)
            || text.Contains("滴滴", StringComparison.Ordinal)
            || text.Contains("DIDI", StringComparison.OrdinalIgnoreCase);
        var hasItinerary = text.Contains("行程单", StringComparison.Ordinal)
            || text.Contains("ITINERARY", StringComparison.OrdinalIgnoreCase);
        return hasPlatform && hasItinerary;
    }

    private static DateOnly? ParseTripStartDate(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?im)(?:行程(?:起止)?时间|Trip\s+Time|Trip\s+Start|行程起止日期)\s*[:：]?\s*(?<date>20\d{2}[-/年]\d{1,2}[-/月]\d{1,2})");
        if (!match.Success)
            match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(?<date>20\d{2}[-/]\d{1,2}[-/]\d{1,2})(?!\d)");
        if (!match.Success) return null;
        var normalized = match.Groups["date"].Value.Replace('年', '-').Replace('月', '-').Replace('/', '-');
        return DateOnly.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    }

    private static decimal? ParseTotalAmount(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?im)(?:合计|总计|Total)\s*[:：]?\s*(?:CNY\s*)?(?<amount>[0-9]+(?:\.[0-9]{1,2})?)\s*(?:元|CNY)?");
        if (!match.Success) return null;
        return decimal.TryParse(match.Groups["amount"].Value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var amount) ? amount : null;
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