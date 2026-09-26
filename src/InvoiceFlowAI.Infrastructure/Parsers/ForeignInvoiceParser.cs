using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using UglyToad.PdfPig.Core;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class ForeignInvoiceParser : IParser
{
    private static readonly System.Text.RegularExpressions.Regex InvoiceNumberPattern = new(
        @"\bInvoice\s*#\s*(?<number>[A-Z0-9-]+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly PdfTextExtractor _extractor;

    public ForeignInvoiceParser() : this(new PdfTextExtractor(new PdfTextExtractionOptions(MaximumPages: 2)))
    {
    }

    public ForeignInvoiceParser(PdfTextExtractor extractor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public string ParserId => "foreign-invoice";
    public string Version => "1.0";
    public int Priority => 420;
    public IReadOnlyList<string> SourceKinds { get; } = ["pdf"];
    public string FailureCode => "FOREIGN_INVOICE_PARSER_FAILED";

    public bool CanParse(ParserWorkItem workItem)
    {
        if (!workItem.SourceKind.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var text = string.Join("\n", _extractor.ExtractPages(workItem.DocumentBytes, CancellationToken.None).Select(page => page.Text));
            return InvoiceNumberPattern.IsMatch(text) && UsdAmountPattern.IsMatch(text);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or PdfDocumentFormatException)
        {
            return false;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex UsdAmountPattern = new(
        @"(?im)^\s*\$\s*[0-9,]+\.\d{2}\s*USD\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

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
        var numberMatch = InvoiceNumberPattern.Match(text);
        if (!numberMatch.Success || !UsdAmountPattern.IsMatch(text))
            return Task.FromResult(NeedsFallback("FOREIGN_INVOICE_MARKER_NOT_FOUND", "PDF does not contain a supported foreign invoice marker."));

        var date = ParseInvoiceDate(text);
        var amount = ParseUsdAmount(text);
        var seller = FindSeller(text);
        var purchaser = ReadAfterLabel(text, "Invoiced To");
        var missing = new List<string>();
        if (date is null) missing.Add("InvoiceDate");
        if (amount is null) missing.Add("Amount");
        if (seller.Length == 0) missing.Add("Seller");
        var invoice = new InvoiceDocument(
            DocumentId: workItem.Candidate.DocumentId.Value,
            InvoiceDate: date,
            Purchaser: purchaser,
            Seller: seller,
            Amount: amount,
            TaxAmount: null,
            TotalAmount: amount,
            InvoiceCode: null,
            InvoiceNumber: numberMatch.Groups["number"].Value,
            DocumentType: InvoiceDocumentType.Other,
            Category: "ForeignInvoice",
            Route: null,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: string.Empty)
        {
            Identity = workItem.Candidate.DocumentId,
            ParserName = ParserId,
            Confidence = missing.Count == 0 ? 0.88m : 0.5m,
        };

        if (missing.Count > 0)
            return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, missing,
                new CandidateFailure("FOREIGN_INVOICE_FIELDS_INCOMPLETE", FailureScope.Candidate, FailureCategory.Document, false,
                    "Foreign invoice fields are incomplete."), ParserOutcomeDisposition.NeedsFallback));

        return Task.FromResult(new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), null));
    }

    private static DateOnly? ParseInvoiceDate(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?im)^\s*Invoice\s+Date\s*:\s*(?<date>[^\r\n]+)");
        if (!match.Success) return null;
        var value = System.Text.RegularExpressions.Regex.Replace(match.Groups["date"].Value,
            @"(?<=\d)(?:st|nd|rd|th)\b", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return DateOnly.TryParse(value, System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    }

    private static decimal? ParseUsdAmount(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text,
            @"(?im)^\s*\$\s*(?<amount>[0-9,]+\.\d{2})\s*USD\b");
        if (matches.Count == 0) return null;
        var value = matches[^1].Groups["amount"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }

    private static string ReadAfterLabel(string text, string label)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();
        var index = Array.FindIndex(lines, line => line.Equals(label, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < lines.Length ? lines[index + 1] : string.Empty;
    }

    private static string FindSeller(string text)
    {
        var invoiceIndex = text.IndexOf("Invoice #", StringComparison.OrdinalIgnoreCase);
        if (invoiceIndex < 0) return string.Empty;
        var prefix = text[..invoiceIndex];
        return prefix.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.Equals("UNPAID", StringComparison.OrdinalIgnoreCase)
                && !line.Equals("INVOICE", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
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