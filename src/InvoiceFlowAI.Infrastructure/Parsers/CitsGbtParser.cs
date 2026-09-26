using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using UglyToad.PdfPig.Core;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class CitsGbtParser : IParser
{
    private static readonly System.Text.RegularExpressions.Regex InvoiceNumberPattern = new(
        @"\b(SCCT\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly PdfTextExtractor _extractor;

    public CitsGbtParser() : this(new PdfTextExtractor(new PdfTextExtractionOptions(MaximumPages: 2)))
    {
    }

    public CitsGbtParser(PdfTextExtractor extractor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public string ParserId => "cits-gbt";
    public string Version => "1.0";
    public int Priority => 430;
    public IReadOnlyList<string> SourceKinds { get; } = ["pdf"];
    public string FailureCode => "CITS_GBT_PARSER_FAILED";

    public bool CanParse(ParserWorkItem workItem)
    {
        if (!workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return IsCitsInvoice(string.Join("\n", _extractor.ExtractPages(workItem.DocumentBytes, CancellationToken.None).Select(page => page.Text)));
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
        var invoiceMatch = InvoiceNumberPattern.Match(text);
        if (!IsCitsInvoice(text) || !invoiceMatch.Success)
            return Task.FromResult(NeedsFallback("CITS_INVOICE_MARKER_NOT_FOUND", "PDF does not contain a CITS/GBT invoice marker."));

        var invoiceDate = ParseInvoiceDate(text);
        var amount = ParseTotalAmount(text);
        var missing = new List<string>();
        if (invoiceDate is null) missing.Add("InvoiceDate");
        if (amount is null) missing.Add("Amount");
        var isFlight = ContainsAny(text, "Online Dom Air", "Flight No", "Airline", "Origin", "Destination");
        var isPurchaserCompany = text.Contains("PFIZER", StringComparison.OrdinalIgnoreCase)
            || text.Contains("辉瑞投资有限公司", StringComparison.Ordinal);
        var invoice = new InvoiceDocument(
            DocumentId: workItem.Candidate.DocumentId.Value,
            InvoiceDate: invoiceDate,
            Purchaser: isPurchaserCompany ? "辉瑞投资有限公司" : "个人",
            Seller: "CITS GBT",
            Amount: amount,
            TaxAmount: null,
            TotalAmount: amount,
            InvoiceCode: null,
            InvoiceNumber: invoiceMatch.Groups[1].Value.ToUpperInvariant(),
            DocumentType: isFlight ? InvoiceDocumentType.FlightTicket : InvoiceDocumentType.Other,
            Category: isFlight ? "Flight" : "TravelService",
            Route: null,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: string.Empty)
        {
            Identity = workItem.Candidate.DocumentId,
            ParserName = ParserId,
            Confidence = missing.Count == 0 ? 0.9m : 0.55m,
        };

        if (missing.Count > 0)
            return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, missing,
                new CandidateFailure("CITS_INVOICE_FIELDS_INCOMPLETE", FailureScope.Candidate, FailureCategory.Document, false,
                    "CITS/GBT invoice fields are incomplete."), ParserOutcomeDisposition.NeedsFallback));

        return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), null));
    }

    private static bool IsCitsInvoice(string text)
    {
        var hasProvider = text.Contains("CITSGBT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CITS - American Express", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CITS-AmericanExpress", StringComparison.OrdinalIgnoreCase)
            || text.Contains("国旅运通", StringComparison.Ordinal);
        return hasProvider && InvoiceNumberPattern.IsMatch(text);
    }

    private static bool ContainsAny(string text, params string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static DateOnly? ParseInvoiceDate(string text)
    {
        var dateSection = System.Text.RegularExpressions.Regex.Match(text,
            @"(?is)\bDate\s*:\s*(?<date>[^\r\n]+)|\bDate\s*:\s*(?<date2>\s*\d{1,2}/\d{1,2}/\d{2})");
        var value = dateSection.Success
            ? (dateSection.Groups["date"].Success ? dateSection.Groups["date"].Value : dateSection.Groups["date2"].Value).Trim()
            : text;
        var shortDate = System.Text.RegularExpressions.Regex.Match(value, @"(?<!\d)(?<day>\d{1,2})/(?<month>\d{1,2})/(?<year>\d{2})(?!\d)");
        if (shortDate.Success
            && int.TryParse(shortDate.Groups["year"].Value, out var year)
            && int.TryParse(shortDate.Groups["month"].Value, out var month)
            && int.TryParse(shortDate.Groups["day"].Value, out var day))
        {
            try { return new DateOnly(2000 + year, month, day); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        var fullDate = System.Text.RegularExpressions.Regex.Match(value,
            @"(?<!\d)(?<year>20\d{2})[-/](?<month>\d{1,2})[-/](?<day>\d{1,2})(?!\d)");
        if (!fullDate.Success) return null;
        try
        {
            return new DateOnly(int.Parse(fullDate.Groups["year"].Value), int.Parse(fullDate.Groups["month"].Value), int.Parse(fullDate.Groups["day"].Value));
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static decimal? ParseTotalAmount(string text)
    {
        const string amount = @"(?<amount>[0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2}|[0-9]+\.[0-9]{2})";
        var patterns = new[]
        {
            $@"(?is)\bTotal\s*:\s*CNY\s*{amount}",
            $@"(?is)\bAmount\s+Received\s*:\s*CNY\s*{amount}",
            $@"(?is)\bGrand\s+Total\s*:\s*.{{0,160}}?{amount}",
        };
        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (match.Success && decimal.TryParse(match.Groups["amount"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                    System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var result))
                return result;
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