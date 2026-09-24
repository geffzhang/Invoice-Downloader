using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using UglyToad.PdfPig.Core;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class AccommodationFolioParser : IParser
{
    private readonly PdfTextExtractor _extractor;

    public AccommodationFolioParser() : this(new PdfTextExtractor())
    {
    }

    public AccommodationFolioParser(PdfTextExtractor extractor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public string ParserId => "accommodation-folio";
    public string Version => "1.0";
    public int Priority => 440;
    public IReadOnlyList<string> SourceKinds { get; } = ["pdf"];
    public string FailureCode => "FOLIO_PARSER_FAILED";

    public bool CanParse(ParserWorkItem workItem)
    {
        if (!workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return ContainsFolioMarker(string.Join("\n", _extractor.ExtractPages(workItem.DocumentBytes, CancellationToken.None).Select(page => page.Text)));
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
        if (!ContainsFolioMarker(text))
            return Task.FromResult(NeedsFallback("FOLIO_MARKER_NOT_FOUND", "PDF does not contain a hotel folio marker."));

        var date = ParseDate(ReadField(text, "Check-out Date", "Checkout Date", "Departure Date", "离店日期", "退房日期"));
        var amount = ParseAmount(ReadField(text, "Payment Total", "Paid Total", "Balance Due", "付款合计", "应付合计"))
            ?? ParseAmount(ReadField(text, "Consumption Total", "Total Amount", "消费合计", "消费总额"));
        var seller = ReadField(text, "Hotel Name", "酒店名称", "酒店");
        if (seller.Length == 0) seller = FindHotelHeading(text);
        var purchaser = ReadField(text, "Guest Name", "Guest", "客人姓名", "宾客姓名");
        var missing = new List<string>();
        if (date is null) missing.Add("InvoiceDate");
        if (amount is null) missing.Add("Amount");
        var invoice = new InvoiceDocument(
            DocumentId: workItem.Candidate.DocumentId.Value,
            InvoiceDate: date,
            Purchaser: purchaser,
            Seller: seller,
            Amount: amount,
            TaxAmount: null,
            TotalAmount: amount,
            InvoiceCode: null,
            InvoiceNumber: null,
            DocumentType: InvoiceDocumentType.HotelFolio,
            Category: "Accommodation",
            Route: null,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: string.Empty)
        {
            Identity = workItem.Candidate.DocumentId,
            ParserName = ParserId,
            Flags = InvoiceFlags.Folio,
            Confidence = missing.Count == 0 ? 0.9m : 0.5m,
        };

        if (missing.Count > 0)
            return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, missing,
                new CandidateFailure("FOLIO_FIELDS_INCOMPLETE", FailureScope.Candidate, FailureCategory.Document, false,
                    "Hotel folio fields are incomplete."), ParserOutcomeDisposition.NeedsFallback));

        return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), null));
    }

    private static bool ContainsFolioMarker(string text) =>
        text.Contains("Guest Folio", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Hotel Folio", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Checkout Bill", StringComparison.OrdinalIgnoreCase)
        || text.Contains("结账单", StringComparison.Ordinal)
        || text.Contains("住宿水单", StringComparison.Ordinal);

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

    private static string FindHotelHeading(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .FirstOrDefault(line => line.Length > 0 && !line.Contains("Folio", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("结账单", StringComparison.Ordinal) && !line.Contains(':') && !line.Contains('：')) ?? string.Empty;

    private static DateOnly? ParseDate(string value)
    {
        var normalized = value.Replace('年', '-').Replace('月', '-').Replace("日", string.Empty, StringComparison.Ordinal)
            .Replace('/', '-').Replace('.', '-').Trim();
        return DateOnly.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    }

    private static decimal? ParseAmount(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("¥", string.Empty, StringComparison.Ordinal).Replace("￥", string.Empty, StringComparison.Ordinal);
        if (normalized.StartsWith('(') && normalized.EndsWith(')')) normalized = "-" + normalized[1..^1];
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Number,
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