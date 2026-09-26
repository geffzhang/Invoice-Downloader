using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using UglyToad.PdfPig;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class BaiwangRecoveryStrategy : IUrlRecoveryStrategy
{
    private const string PreviewEndpoint = "https://pis.baiwang.com/bwmg/mix/bw/previewInvoiceQd";
    private const string DownloadEndpoint = "https://pis.baiwang.com/bwmg/mix/bw/downloadFormat";
    private static readonly string[] Formats = ["XML", "PDF", "OFD"];
    private static readonly string[] WrapperMarkers = ["发票预览", "下载pdf", "下载ofd", "下载xml", "关于百望", "previewinvoice", "downloadpdf", "downloadofd", "downloadxml"];
    private static readonly string[] StructuredMarkers = ["发票号码", "发票代码", "购买方名称", "销售方名称", "价税合计", "开票日期", "发票金额", "invoice_number", "seller", "buyer"];
    private readonly PublicUrlRecoveryClient _client;
    private readonly DirectArtifactProbe _probe;

    public BaiwangRecoveryStrategy(PublicUrlRecoveryClient client, DirectArtifactProbe probe)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public IReadOnlyCollection<string> ProviderFamilies { get; } = ["baiwang"];

    public async Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var artifacts = new List<CapturedUrlArtifact>();
        for (var ordinal = 0; ordinal < group.Candidates.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceUrl = group.Candidates[ordinal].SourceUrl;
            var direct = await _probe.ProbeAsync(sourceUrl, ordinal, cancellationToken).ConfigureAwait(false);
            artifacts.AddRange(direct.Select(InspectPdf));

            var query = ParseQuery(sourceUrl.Query);
            if (!IsBaiwangHost(sourceUrl.IdnHost)
                || !query.TryGetValue("param", out var parameter)
                || string.IsNullOrWhiteSpace(parameter))
            {
                continue;
            }

            var response = await _client.SendFollowingRedirectsAsync(
                new Uri(PreviewEndpoint), HttpMethod.Post, null, cancellationToken,
                Encoding.UTF8.GetBytes(parameter), "application/json; charset=utf-8").ConfigureAwait(false);
            using var preview = JsonDocument.Parse(response.Response.Content);
            if (!preview.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            foreach (var format in Formats)
            {
                var downloadUrl = new Uri($"{DownloadEndpoint}?param={Uri.EscapeDataString(parameter)}&formatType={format}");
                var captures = await _probe.ProbeAsync(downloadUrl, ordinal, cancellationToken).ConfigureAwait(false);
                artifacts.AddRange(captures.Select(InspectPdf));
            }
        }

        return BaiwangArtifactSelector.Select(group.ExpectedFields, artifacts);
    }

    private static CapturedUrlArtifact InspectPdf(CapturedUrlArtifact artifact)
    {
        if (artifact.Kind != RecoveredArtifactKind.Pdf) return artifact;
        string text;
        try
        {
            using var document = PdfDocument.Open(artifact.Content.ToArray());
            text = string.Join('\n', document.GetPages().Select(static page => page.Text));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException
            or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            text = string.Empty;
        }

        var compact = Compact(text);
        var wrapperHits = WrapperMarkers.Count(marker => compact.Contains(Compact(marker), StringComparison.OrdinalIgnoreCase));
        var structured = StructuredMarkers.Any(marker => compact.Contains(Compact(marker), StringComparison.OrdinalIgnoreCase));
        var fields = ExtractPdfFields(text);
        return wrapperHits >= 2 && !structured
            ? artifact with { InvoiceFields = fields, ExpectedMatch = false, MatchReasonCode = "BAIWANG_WRAPPER_DETECTED" }
            : artifact with { InvoiceFields = fields };
    }

    private static IReadOnlyDictionary<string, string> ExtractPdfFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(fields, "invoice_number", Regex.Match(text, @"发票号码\s*[:：]?\s*([0-9]{8,})"));
        Add(fields, "invoice_code", Regex.Match(text, @"发票代码\s*[:：]?\s*([0-9]{8,})"));
        Add(fields, "seller", Regex.Match(text, @"销售方名称\s*[:：]?\s*([^\s]+)"));
        Add(fields, "purchaser", Regex.Match(text, @"购买方名称\s*[:：]?\s*([^\s]+)"));
        var date = Regex.Match(text, @"开票日期\s*[:：]?\s*(20\d{2}[-/.年]\d{1,2}[-/.月]\d{1,2})");
        if (date.Success && DateTime.TryParse(date.Groups[1].Value.Replace('年', '-').Replace('月', '-'), CultureInfo.InvariantCulture, out var parsedDate))
        {
            fields["invoice_date"] = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var amount = Regex.Match(text, @"(?:价税合计|发票金额)[^0-9]*([0-9]+(?:\.[0-9]{2})?)");
        if (amount.Success) fields["amount"] = amount.Groups[1].Value.Contains('.') ? amount.Groups[1].Value : amount.Groups[1].Value + ".00";
        return fields;
    }

    private static void Add(Dictionary<string, string> fields, string key, Match match)
    {
        if (match.Success && match.Groups[1].Value.Length > 0) fields[key] = match.Groups[1].Value.Trim();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator].Replace('+', ' '));
            var value = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..].Replace('+', ' '));
            if (key.Length > 0) values.TryAdd(key, value);
        }
        return values;
    }

    private static bool IsBaiwangHost(string host)
        => host.Equals("baiwang.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".baiwang.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("efapiao.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".efapiao.com", StringComparison.OrdinalIgnoreCase);

    private static string Compact(string value) => string.Concat(value.Where(static character => !char.IsWhiteSpace(character))).ToLowerInvariant();
}