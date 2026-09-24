using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed record ParsedPdfInvoiceFields(
    DateOnly? InvoiceDate,
    string InvoiceCode,
    string Purchaser,
    string Seller,
    decimal? Amount,
    decimal? TaxAmount,
    decimal? TotalAmount,
    string InvoiceNumber,
    InvoiceDocumentType DocumentType,
    IReadOnlyList<string> MissingFields);

public sealed class PdfFieldParser
{
    private static readonly Regex FieldPattern = new(
        @"^(?<name>[^:：]+)\s*[:：]\s*(?<value>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public ParsedPdfInvoiceFields Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = FieldPattern.Match(line.Trim());
            if (match.Success)
            {
                fields.TryAdd(NormalizeKey(match.Groups["name"].Value), match.Groups["value"].Value.Trim());
            }
        }

        var number = Get(fields, "发票号码", "Invoice Number", "Invoice No", "Number");
        var code = Get(fields, "发票代码", "Invoice Code", "Code");
        var date = ParseDate(Get(fields, "开票日期", "Invoice Date", "Issue Date", "Date"));
        var amount = ParseDecimal(Get(fields, "金额", "发票金额", "Amount", "Subtotal"));
        var tax = ParseDecimal(Get(fields, "税额", "Tax Amount", "Tax"));
        var total = ParseDecimal(Get(fields, "价税合计", "合计", "Total Amount", "Total"));
        var purchaser = Get(fields, "购买方名称", "Purchaser", "Buyer");
        var seller = NormalizeSeller(Get(fields, "销售方名称", "Seller", "Seller Name"));
        var type = ResolveType(text);
        var missing = new List<string>();
        if (number.Length == 0) missing.Add("InvoiceNumber");
        if (date is null) missing.Add("InvoiceDate");
        if (amount is null) missing.Add("Amount");
        if (seller.Length == 0) missing.Add("Seller");
        return new ParsedPdfInvoiceFields(date, code, purchaser, seller, amount, tax, total, number, type, missing);
    }

    private static string Get(IReadOnlyDictionary<string, string> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(NormalizeKey(name), out var value)) return value;
        }
        return string.Empty;
    }

    private static string NormalizeKey(string value) =>
        Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string NormalizeSeller(string value)
    {
        var normalized = value.Trim();
        while (normalized.StartsWith("名称:", StringComparison.Ordinal)
            || normalized.StartsWith("名称：", StringComparison.Ordinal))
        {
            normalized = normalized[3..].TrimStart();
        }
        return normalized;
    }

    private static DateOnly? ParseDate(string value)
    {
        var normalized = value.Replace('年', '-').Replace('月', '-').Replace('日', ' ').Replace('/', '-').Replace('.', '-').Trim();
        return DateOnly.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date
            : null;
    }

    private static decimal? ParseDecimal(string value)
    {
        var normalized = value.Trim().Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("￥", string.Empty, StringComparison.Ordinal)
            .Replace("¥", string.Empty, StringComparison.Ordinal);
        if (normalized.StartsWith('(') && normalized.EndsWith(')')) normalized = "-" + normalized[1..^1];
        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowParentheses,
            CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }

    private static InvoiceDocumentType ResolveType(string text)
    {
        if (text.Contains("火车票", StringComparison.Ordinal) || text.Contains("铁路电子客票", StringComparison.Ordinal)) return InvoiceDocumentType.TrainTicket;
        if (text.Contains("行程单", StringComparison.Ordinal)) return InvoiceDocumentType.Itinerary;
        if (text.Contains("出租车", StringComparison.Ordinal)) return InvoiceDocumentType.Taxi;
        if (text.Contains("餐饮", StringComparison.Ordinal) || text.Contains("Catering", StringComparison.OrdinalIgnoreCase)) return InvoiceDocumentType.Catering;
        if (text.Contains("电子发票", StringComparison.Ordinal) || text.Contains("数电发票", StringComparison.Ordinal)
            || text.Contains("VAT Invoice", StringComparison.OrdinalIgnoreCase)) return InvoiceDocumentType.VatInvoice;
        return InvoiceDocumentType.Other;
    }
}