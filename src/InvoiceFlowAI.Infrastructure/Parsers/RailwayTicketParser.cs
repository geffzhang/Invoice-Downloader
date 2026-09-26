using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using UglyToad.PdfPig.Core;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class RailwayTicketParser : IParser
{
    private readonly PdfTextExtractor _extractor;
    private readonly PdfFieldParser _fieldParser;

    public RailwayTicketParser() : this(new PdfTextExtractor(), new PdfFieldParser())
    {
    }

    public RailwayTicketParser(PdfTextExtractor extractor, PdfFieldParser fieldParser)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _fieldParser = fieldParser ?? throw new ArgumentNullException(nameof(fieldParser));
    }

    public string ParserId => "railway-ticket";
    public string Version => "1.0";
    public int Priority => 450;
    public IReadOnlyList<string> SourceKinds { get; } = ["pdf"];
    public string FailureCode => "RAILWAY_PARSER_FAILED";

    public bool CanParse(ParserWorkItem workItem)
    {
        if (!workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return ContainsRailwayMarker(string.Join("\n", _extractor.ExtractPages(workItem.DocumentBytes, CancellationToken.None).Select(page => page.Text)));
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
        if (!ContainsRailwayMarker(text))
            return Task.FromResult(NeedsFallback("RAILWAY_MARKER_NOT_FOUND", "PDF does not contain a railway ticket marker."));

        var fields = _fieldParser.Parse(text);
        var departureDate = ReadDate(text, "Departure Date", "乘车日期", "乘车时间")
            ?? ReadLeadingDateBeforeMarker(text);
        var effectiveDate = departureDate ?? fields.InvoiceDate;
        var missing = fields.MissingFields.Where(field => field != "InvoiceDate" || effectiveDate is null).ToList();
        var departureCity = ReadField(text, "Departure City", "出发站", "始发站");
        var destinationCity = ReadField(text, "Destination City", "到达站", "终到站");
        var invoice = new InvoiceDocument(
            DocumentId: workItem.Candidate.DocumentId.Value,
            InvoiceDate: effectiveDate,
            Purchaser: fields.Purchaser,
            Seller: string.IsNullOrWhiteSpace(fields.Seller) ? "中国铁路" : fields.Seller,
            Amount: fields.Amount,
            TaxAmount: fields.TaxAmount,
            TotalAmount: fields.TotalAmount,
            InvoiceCode: fields.InvoiceCode,
            InvoiceNumber: fields.InvoiceNumber,
            DocumentType: InvoiceDocumentType.TrainTicket,
            Category: "TrainTicket",
            Route: new InvoiceRoute(InvoiceRouteDirection.Unknown, departureDate, departureCity, destinationCity),
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: string.Empty)
        {
            Identity = workItem.Candidate.DocumentId,
            ParserName = ParserId,
            Confidence = missing.Count == 0 ? 0.95m : 0.55m,
        };

        if (missing.Count > 0)
            return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, missing,
                new CandidateFailure("RAILWAY_FIELDS_INCOMPLETE", FailureScope.Candidate, FailureCategory.Document, false,
                    "Railway ticket fields are incomplete."), ParserOutcomeDisposition.NeedsFallback));

        return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), null));
    }

    private static bool ContainsRailwayMarker(string text) =>
        text.Contains("铁路电子客票", StringComparison.Ordinal)
        || text.Contains("火车票", StringComparison.Ordinal)
        || text.Contains("Railway Ticket", StringComparison.OrdinalIgnoreCase);

    private static string ReadField(string text, params string[] names)
    {
        foreach (var name in names)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text,
                $@"(?im)^\s*{System.Text.RegularExpressions.Regex.Escape(name)}\s*[:：]\s*(?<value>[^\r\n]+)");
            if (match.Success) return match.Groups["value"].Value.Trim();
        }
        return string.Empty;
    }

    private static DateOnly? ReadDate(string text, params string[] names)
    {
        var value = ReadField(text, names);
        if (value.Length == 0) return null;
        var normalized = value.Replace('年', '-').Replace('月', '-').Replace("日", string.Empty, StringComparison.Ordinal)
            .Replace('/', '-').Trim();
        return DateOnly.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    }

    private static DateOnly? ReadLeadingDateBeforeMarker(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var markerIndex = Array.FindIndex(lines, line => ContainsRailwayMarker(line));
        if (markerIndex <= 0) return null;
        for (var index = markerIndex - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(line,
                    @"^20\d{2}(?:[-/.年])\d{1,2}(?:[-/.月])\d{1,2}日?$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                continue;
            var normalized = line.Replace('年', '-').Replace('月', '-').Replace("日", string.Empty, StringComparison.Ordinal)
                .Replace('/', '-').Replace('.', '-');
            if (DateOnly.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var date))
                return date;
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