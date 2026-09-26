using System.Net;
using System.Text.RegularExpressions;
using InvoiceFlowAI.Application.Mail;

namespace InvoiceFlowAI.Infrastructure.Mail;

internal static partial class MailboxUrlCandidateDiscovery
{
    private static readonly Regex AnchorHrefPattern = new(
        "href\\s*=\\s*(?:\"(?<quoted>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AbsoluteUrlPattern = new(
        "https?://[^\\s<>\"']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QueryFileCodePattern = new(
        "(?:^|&)filecode?=(?<value>[^&]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] BaiwangUrlTokens =
    [
        "u.baiwang.com",
        "previewinvoiceall",
        "previewinvoice",
        "smkp-vue",
        "maillink",
        "downloadpdf",
        "downloadofd",
        "downloadxml",
    ];

    public static IReadOnlyList<Uri> Extract(string? bodyText, string? htmlBody)
    {
        var urls = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        AddMatches(bodyText, AbsoluteUrlPattern, match => match.Value, urls, seen);
        if (!string.IsNullOrEmpty(htmlBody))
        {
            foreach (Match match in AnchorHrefPattern.Matches(htmlBody))
            {
                var value = match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["single"].Success
                        ? match.Groups["single"].Value
                        : match.Groups["bare"].Value;
                AddUrl(WebUtility.HtmlDecode(value), urls, seen);
            }

            AddMatches(WebUtility.HtmlDecode(htmlBody), AbsoluteUrlPattern, match => match.Value, urls, seen);
        }

        return urls;
    }

    public static string DetectProviderFamily(Uri url, string? senderAddress, string? subject)
    {
        var host = url.IdnHost.ToLowerInvariant();
        var path = url.AbsolutePath.ToLowerInvariant();

        if (host == "dppt.beijing.chinatax.gov.cn"
            && path.Contains("/kpfw/fpjfzz/v1/exportdzfpwjewm", StringComparison.Ordinal))
        {
            return "chinatax_direct_invoice";
        }

        if (host == "fp.bwjf.cn" && (path.StartsWith("/u/", StringComparison.Ordinal) || path.StartsWith("/downsigninvoice", StringComparison.Ordinal)))
        {
            return "bwjf_signed_invoice";
        }

        if (url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && url.IsDefaultPort
            && host == "sdapi.fpyun.com.cn"
            && path == "/invoice/qd/download/getinvoicefile")
        {
            return "fpyun_direct_invoice";
        }

        if (host == "nnfp.jss.com.cn"
            && (path == "/scan-invoice/printqrcode"
                || path.StartsWith("/invoice/scan/", StringComparison.Ordinal)
                || Regex.IsMatch(path, "^/[a-z0-9=_-]{8,}$", RegexOptions.CultureInvariant)))
        {
            return "nuonuo_scan_invoice";
        }

        if (host == "files.pdd-fapiao.com"
            && path.StartsWith("/invoice/", StringComparison.Ordinal)
            && (path.Contains("/pdf/", StringComparison.Ordinal)
                || path.Contains("/xml/", StringComparison.Ordinal)
                || path.Contains("/ofd/", StringComparison.Ordinal)))
        {
            return "pdd_direct_invoice";
        }

        if (host.StartsWith("eicore-invoice-", StringComparison.Ordinal)
            && host.EndsWith(".s3.cn-north-1.jdcloud-oss.com", StringComparison.Ordinal)
            && path.StartsWith("/digital-invoice/", StringComparison.Ordinal)
            && (path.EndsWith(".pdf", StringComparison.Ordinal)
                || path.EndsWith(".xml", StringComparison.Ordinal)
                || path.EndsWith(".ofd", StringComparison.Ordinal)))
        {
            return "jdcloud_direct_invoice";
        }

        if (host == "etd.kpbyd.com"
            && path == "/hub/files/download"
            && QueryFileCodePattern.Match(url.Query.TrimStart('?')).Groups["value"].Success
            && Uri.UnescapeDataString(QueryFileCodePattern.Match(url.Query.TrimStart('?')).Groups["value"].Value)
                .ToLowerInvariant() is var fileCode
            && (fileCode.EndsWith("_pdf", StringComparison.Ordinal)
                || fileCode.EndsWith("_xml", StringComparison.Ordinal)
                || fileCode.EndsWith("_ofd", StringComparison.Ordinal)))
        {
            return "kpbyd_direct_invoice";
        }

        var lowerUrl = url.AbsoluteUri.ToLowerInvariant();
        var compactSubject = Regex.Replace(subject ?? string.Empty, "\\s+", string.Empty).ToLowerInvariant();
        if (host.Contains("baiwang", StringComparison.Ordinal)
            || host.EndsWith("efapiao.com", StringComparison.Ordinal)
            || BaiwangUrlTokens.Any(token => lowerUrl.Contains(token, StringComparison.Ordinal))
            || (senderAddress ?? string.Empty).Contains("baiwang", StringComparison.OrdinalIgnoreCase)
            || compactSubject.Contains("电子发票下载", StringComparison.Ordinal)
            || compactSubject.Contains("发票下载", StringComparison.Ordinal))
        {
            return "baiwang";
        }

        return string.Empty;
    }

    public static IReadOnlyDictionary<string, string> ExtractExpectedFields(
        string providerFamily,
        Uri url,
        string? subject,
        string? bodyText)
    {
        return providerFamily switch
        {
            "baiwang" => ExtractBaiwangFields(bodyText),
            "chinatax_direct_invoice" or "bwjf_signed_invoice" or "fpyun_direct_invoice"
                or "nuonuo_scan_invoice" or "pdd_direct_invoice" or "jdcloud_direct_invoice"
                or "kpbyd_direct_invoice" => ExtractDirectInvoiceFields(url, subject, bodyText),
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<ExpectedFieldEvidence>> ExtractExpectedFieldEvidence(
        string providerFamily,
        Uri url,
        string? subject,
        string? bodyText,
        long discoveryOrder)
    {
        var evidence = new Dictionary<string, List<ExpectedFieldEvidence>>(StringComparer.Ordinal);
        if (providerFamily == "baiwang")
        {
            foreach (var field in ExtractBaiwangFields(bodyText))
            {
                AddEvidence(evidence, field.Key, field.Value, ExpectedFieldSource.Body, discoveryOrder);
            }
        }
        else if (providerFamily is "chinatax_direct_invoice" or "bwjf_signed_invoice" or "fpyun_direct_invoice"
            or "nuonuo_scan_invoice" or "pdd_direct_invoice" or "jdcloud_direct_invoice"
            or "kpbyd_direct_invoice")
        {
            var query = ParseQuery(url.Query);
            AddEvidence(evidence, "invoice_number", QueryValue(query, "Fphm", "fphm"), ExpectedFieldSource.UrlQuery, discoveryOrder);
            AddEvidence(evidence, "seller", QueryValue(query, "sellerName", "sellername"), ExpectedFieldSource.UrlQuery, discoveryOrder);
            AddEvidence(evidence, "invoice_date", NormalizeDate(QueryValue(query, "Kprq", "kprq")), ExpectedFieldSource.UrlQuery, discoveryOrder);

            var queryKind = QueryValue(query, "Wjgs", "wjgs", "jflx")?.ToLowerInvariant();
            var fileCode = QueryValue(query, "fileCode", "filecode")?.ToLowerInvariant();
            if (queryKind is "pdf" or "xml" or "ofd")
            {
                AddEvidence(evidence, "preferred_kind", queryKind, ExpectedFieldSource.UrlQuery, discoveryOrder);
            }
            else if (fileCode is not null && (fileCode.EndsWith("_pdf", StringComparison.Ordinal)
                || fileCode.EndsWith("_xml", StringComparison.Ordinal)
                || fileCode.EndsWith("_ofd", StringComparison.Ordinal)))
            {
                AddEvidence(evidence, "preferred_kind", fileCode[^3..], ExpectedFieldSource.UrlQuery, discoveryOrder);
            }

            var subjectText = Regex.Replace(subject ?? string.Empty, "\\s+", " ").Trim();
            var body = Regex.Replace(bodyText ?? string.Empty, "\\s+", " ").Trim();
            AddEvidence(evidence, "invoice_number",
                MatchGroup(subjectText, "发票(?:号码|号碼|号):?\\s*([0-9]{8,20})")
                    ?? MatchGroup(subjectText, "(?<!\\d)(\\d{20})(?!\\d)"),
                ExpectedFieldSource.Subject, discoveryOrder);
            AddEvidence(evidence, "invoice_number",
                MatchGroup(body, "发票(?:号码|号碼|号):?\\s*([0-9]{8,20})")
                    ?? MatchGroup(body, "(?<!\\d)(\\d{20})(?!\\d)"),
                ExpectedFieldSource.Body, discoveryOrder);

            var sellerPatterns = new[]
            {
                "您收到一张【(.+?)】开具的发票",
                "来自【(.+?)】为您开具的电子发票",
                "您收到来自(.+?)的电子发票",
                "来自【(.+?)】开具的发票",
            };
            foreach (var pattern in sellerPatterns)
            {
                var seller = MatchGroup(subjectText, pattern);
                if (!string.IsNullOrEmpty(seller))
                {
                    AddEvidence(evidence, "seller", seller, ExpectedFieldSource.Subject, discoveryOrder);
                    break;
                }
            }

            var bodyDate = NormalizeDate(MatchGroup(body, "开票日期\\s*[:：]?\\s*(20\\d{2}[-/.年]\\s*\\d{1,2}[-/.月]\\s*\\d{1,2})")
                ?? MatchGroup(body, "(20\\d{2}[-/.年]\\d{1,2}[-/.月]\\d{1,2}|20\\d{6})"));
            AddEvidence(evidence, "invoice_date", bodyDate, ExpectedFieldSource.Body, discoveryOrder);
            if (bodyDate is null)
            {
                AddEvidence(evidence, "invoice_date",
                    NormalizeDate(MatchGroup(subjectText, "(20\\d{2}[-/.年]\\d{1,2}[-/.月]\\d{1,2}|20\\d{6})")),
                    ExpectedFieldSource.Subject, discoveryOrder);
            }

            if (!evidence.ContainsKey("preferred_kind"))
            {
                var extension = Path.GetExtension(url.AbsolutePath).TrimStart('.').ToLowerInvariant();
                AddEvidence(evidence, "preferred_kind", extension is "pdf" or "xml" or "ofd" ? extension : "pdf",
                    ExpectedFieldSource.UrlQuery, discoveryOrder);
            }
        }

        return evidence.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<ExpectedFieldEvidence>)pair.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }

    private static void AddEvidence(
        IDictionary<string, List<ExpectedFieldEvidence>> evidence,
        string field,
        string? value,
        ExpectedFieldSource source,
        long discoveryOrder)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!evidence.TryGetValue(field, out var values))
        {
            values = [];
            evidence.Add(field, values);
        }

        if (!values.Any(item => item.Source == source && item.Value.Equals(value.Trim(), StringComparison.Ordinal)))
        {
            values.Add(new ExpectedFieldEvidence(value.Trim(), source, discoveryOrder));
        }
    }

    private static IReadOnlyDictionary<string, string> ExtractDirectInvoiceFields(Uri url, string? subject, string? bodyText)
    {
        var query = ParseQuery(url.Query);
        var subjectText = Regex.Replace(subject ?? string.Empty, "\\s+", " ").Trim();
        var body = Regex.Replace(bodyText ?? string.Empty, "\\s+", " ").Trim();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        var invoiceNumber = QueryValue(query, "Fphm", "fphm");
        if (string.IsNullOrEmpty(invoiceNumber))
        {
            invoiceNumber = MatchGroup(subjectText, "发票(?:号码|号碼|号):?\\s*([0-9]{8,20})")
                ?? MatchGroup($"{subjectText} {body}", "(?<!\\d)(\\d{20})(?!\\d)")
                ?? MatchGroup(url.AbsoluteUri, "(?<!\\d)(\\d{20})(?!\\d)");
        }

        var seller = QueryValue(query, "sellerName", "sellername");
        if (string.IsNullOrEmpty(seller))
        {
            foreach (var pattern in new[]
                     {
                         "您收到一张【(.+?)】开具的发票",
                         "来自【(.+?)】为您开具的电子发票",
                         "您收到来自(.+?)的电子发票",
                         "来自【(.+?)】开具的发票",
                     })
            {
                seller = MatchGroup(subjectText, pattern);
                if (!string.IsNullOrEmpty(seller)) break;
            }
        }

        var invoiceDate = QueryValue(query, "Kprq", "kprq");
        invoiceDate = NormalizeDate(invoiceDate)
            ?? NormalizeDate(MatchGroup(body, "开票日期\\s*[:：]?\\s*(20\\d{2}[-/.年]\\s*\\d{1,2}[-/.月]\\s*\\d{1,2})"))
            ?? NormalizeDate(MatchGroup($"{body} {subjectText}", "(20\\d{2}[-/.年]\\d{1,2}[-/.月]\\d{1,2}|20\\d{6})"));

        var preferredKind = QueryValue(query, "Wjgs", "wjgs", "jflx")?.ToLowerInvariant();
        var fileCode = QueryValue(query, "fileCode", "filecode")?.ToLowerInvariant();
        if (string.IsNullOrEmpty(preferredKind))
        {
            preferredKind = fileCode is not null && fileCode.EndsWith("_xml", StringComparison.Ordinal) ? "xml"
                : fileCode is not null && fileCode.EndsWith("_ofd", StringComparison.Ordinal) ? "ofd"
                : fileCode is not null && fileCode.EndsWith("_pdf", StringComparison.Ordinal) ? "pdf"
                : Path.GetExtension(url.AbsolutePath).TrimStart('.').ToLowerInvariant();
        }

        AddField(fields, "invoice_number", invoiceNumber);
        AddField(fields, "seller", seller);
        AddField(fields, "invoice_date", invoiceDate);
        AddField(fields, "preferred_kind", preferredKind is "pdf" or "xml" or "ofd" ? preferredKind : "pdf");
        return fields;
    }

    private static IReadOnlyDictionary<string, string> ExtractBaiwangFields(string? bodyText)
    {
        var body = Regex.Replace(bodyText ?? string.Empty, "\\s+", " ").Trim();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var seller = MatchGroup(body, "(?:用户，您好[:：]?\\s*|您好[:：]?\\s*)(.+?)为您开具了电子发票")
            ?? MatchGroup(body, "(.+?)为您开具了电子发票")
            ?? MatchGroup(body, "来自[【\\[](.+?)[】\\]]开具的发票")
            ?? MatchGroup(body, "seller[:：]?\\s*([^\\s]+)", RegexOptions.IgnoreCase);
        var purchaser = MatchGroup(body, "购买方名称[:：]?\\s*([^\\s]+)")
            ?? MatchGroup(body, "buyer[:：]?\\s*([^\\s]+)", RegexOptions.IgnoreCase);
        var amount = MatchGroup(body, "(?:发票金额|价税合计|amount)[:：]?\\s*([0-9]+\\.[0-9]{2})", RegexOptions.IgnoreCase);
        if (amount is null)
        {
            amount = Regex.Matches(body, "[0-9]+\\.[0-9]{2}")
                .Select(static match => match.Value)
                .OrderByDescending(static value => decimal.TryParse(value, out var number) ? number : decimal.MinValue)
                .FirstOrDefault();
        }

        var invoiceDate = MatchGroup(body, "(?:开票日期|issue\\s*time|issue\\s*date|request\\s*time|date)[:：]?\\s*(20\\d{2}[-/.年]\\d{1,2}[-/.月]\\d{1,2})", RegexOptions.IgnoreCase)
            ?? MatchGroup(body, "(20\\d{2}[-/.年]\\d{1,2}[-/.月]\\d{1,2})");
        var invoiceNumber = MatchGroup(body, "(?:发票号码|invoice\\s*number)[:：]?\\s*([0-9]{8,})", RegexOptions.IgnoreCase)
            ?? MatchGroup(body, "(?<!\\d)(\\d{20})(?!\\d)");
        var invoiceCode = MatchGroup(body, "(?:发票代码|invoice\\s*code)[:：]?\\s*([0-9]{8,})", RegexOptions.IgnoreCase);

        AddField(fields, "seller", seller);
        AddField(fields, "purchaser", purchaser);
        AddField(fields, "amount", amount);
        AddField(fields, "invoice_date", NormalizeDate(invoiceDate));
        AddField(fields, "invoice_number", invoiceNumber);
        AddField(fields, "invoice_code", invoiceCode);
        return fields;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = DecodeQueryComponent(separator < 0 ? pair : pair[..separator]);
            var value = DecodeQueryComponent(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            if (!string.IsNullOrEmpty(key) && !values.ContainsKey(key)) values.Add(key, value);
        }

        return values;
    }

    private static string DecodeQueryComponent(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string? QueryValue(IReadOnlyDictionary<string, string> query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? MatchGroup(string source, string pattern, RegexOptions options = RegexOptions.CultureInvariant)
    {
        var match = Regex.Match(source, pattern, options | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, "(20\\d{2})[-/.年]?(\\d{1,2})[-/.月]?(\\d{1,2})");
        return match.Success
            && int.TryParse(match.Groups[2].Value, out var month)
            && int.TryParse(match.Groups[3].Value, out var day)
            && month is >= 1 and <= 12
            && day is >= 1 and <= 31
                ? $"{match.Groups[1].Value}-{month:00}-{day:00}"
                : null;
    }

    private static void AddField(IDictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields[key] = value.Trim();
    }

    private static void AddMatches(
        string? source,
        Regex pattern,
        Func<Match, string> selector,
        ICollection<Uri> urls,
        ISet<string> seen)
    {
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        foreach (Match match in pattern.Matches(source))
        {
            AddUrl(selector(match), urls, seen);
        }
    }

    private static void AddUrl(string? value, ICollection<Uri> urls, ISet<string> seen)
    {
        var normalized = WebUtility.HtmlDecode(value ?? string.Empty).Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var url)
            || (!url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(url.UserInfo)
            || !seen.Add(url.AbsoluteUri))
        {
            return;
        }

        urls.Add(url);
    }
}