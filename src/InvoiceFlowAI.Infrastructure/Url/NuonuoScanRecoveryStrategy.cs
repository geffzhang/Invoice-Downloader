using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class NuonuoScanRecoveryStrategy : IUrlRecoveryStrategy
{
    private static readonly string[] ArtifactUrlProperties = ["xmlUrl", "url", "ofdDownloadUrl"];
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    private readonly PublicUrlRecoveryClient _client;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public NuonuoScanRecoveryStrategy(
        PublicUrlRecoveryClient client,
        TimeSpan? timeout = null,
        int maxAttempts = 3,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _maxAttempts = maxAttempts;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public IReadOnlyCollection<string> ProviderFamilies { get; } = ["nuonuo_scan_invoice"];

    public async Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await RecoverOnceAsync(group, cancellationToken).ConfigureAwait(false);
            }
            catch (UrlRecoveryException exception) when (
                attempt + 1 < _maxAttempts
                && exception.ReasonCode is not "URL_POLICY_REJECTED"
                    and not "NUONUO_MISSING_PARAM_LIST"
                    and not "NUONUO_ARTIFACT_SELECTION_AMBIGUOUS")
            {
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                if (delay > TimeSpan.Zero)
                {
                    await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException("Nuonuo recovery retry loop exited without a result.");
    }

    private async Task<UrlRecoveryResult> RecoverOnceAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        if (group.Candidates.Count == 0)
        {
            throw new ArgumentException("URL recovery group must contain candidates.", nameof(group));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var artifacts = new List<CapturedUrlArtifact>();
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            for (var sourceOrdinal = 0; sourceOrdinal < group.Candidates.Count; sourceOrdinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var shortLink = await _client.SendFollowingRedirectsAsync(
                    group.Candidates[sourceOrdinal].SourceUrl, HttpMethod.Get, null, deadline.Token).ConfigureAwait(false);
                var query = ParseQuery(shortLink.EffectiveUrl.Query);
                if (!query.TryGetValue("paramList", out var paramList) || string.IsNullOrWhiteSpace(paramList))
                {
                    throw new UrlRecoveryException(
                        "NUONUO_MISSING_PARAM_LIST",
                        "Invoice link did not contain required scan parameters.",
                        false,
                        false);
                }

                var endpoint = query.TryGetValue("isOuterPageReq", out var outerPage)
                    && outerPage.Equals("true", StringComparison.OrdinalIgnoreCase)
                        ? "https://nnfp.jss.com.cn/sapi/invoice/scan/IvcDetail.do"
                        : "https://nnfp.jss.com.cn/sapi/scan2/getIvcDetailShow.do";
                var form = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["paramList"] = paramList,
                    ["code"] = query.GetValueOrDefault("code", string.Empty),
                    ["aliView"] = query.GetValueOrDefault("aliView", string.Empty),
                    ["invoiceDetailMiddleUri"] = "printQrcode",
                    ["shortLinkSource"] = query.GetValueOrDefault("shortLinkSource", string.Empty),
                };
                var detailResponse = await _client.SendFollowingRedirectsAsync(
                    new Uri(endpoint), HttpMethod.Post, form, deadline.Token).ConfigureAwait(false);
                using var detail = JsonDocument.Parse(detailResponse.Response.Content);
                if (!detail.RootElement.TryGetProperty("status", out var status)
                    || status.GetString() != "0000"
                    || !detail.RootElement.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("invoiceSimpleVo", out var invoice))
                {
                    throw new UrlRecoveryException(
                        "NUONUO_DETAIL_API_UNSUCCESSFUL",
                        "Invoice provider did not return invoice download details.",
                        true,
                        false);
                }

                foreach (var propertyName in ArtifactUrlProperties)
                {
                    if (!invoice.TryGetProperty(propertyName, out var value)
                        || value.ValueKind != JsonValueKind.String
                        || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var target)
                        || !seenTargets.Add(target.AbsoluteUri))
                    {
                        continue;
                    }

                    try
                    {
                        var downloaded = await _client.SendFollowingRedirectsAsync(
                            target, HttpMethod.Get, null, deadline.Token).ConfigureAwait(false);
                        var legacyResult = new UrlRecoveryResult(downloaded.Response.Content, downloaded.Response.ContentType);
                        var score = PublicUrlRecoveryClient.GetArtifactScore((legacyResult, downloaded.EffectiveUrl));
                        if (score == 0 || !PublicUrlRecoveryClient.HasValidArtifactSignature(downloaded.Response.Content, score))
                        {
                            continue;
                        }

                        var kind = score switch
                        {
                            3 => RecoveredArtifactKind.Pdf,
                            2 => RecoveredArtifactKind.Xml,
                            1 => RecoveredArtifactKind.Ofd,
                            _ => throw new InvalidOperationException(),
                        };
                        var digest = Convert.ToHexString(SHA256.HashData(downloaded.Response.Content.Span)).ToLowerInvariant();
                        artifacts.Add(new CapturedUrlArtifact(
                            kind,
                            downloaded.Response.ContentType,
                            downloaded.Response.Content,
                            sourceOrdinal,
                            digest,
                            SanitizedOrigin(downloaded.EffectiveUrl),
                            new Dictionary<string, string>(StringComparer.Ordinal),
                            null,
                            "ARTIFACT_CAPTURED"));
                    }
                    catch (UrlRecoveryException exception) when (exception.ReasonCode is "URL_RECOVERY_HTTP_FAILED" or "URL_RECOVERY_WORKER_FAILED")
                    {
                    }
                }
            }

            var selectedIndex = SelectPrimary(artifacts);
            if (selectedIndex is null)
            {
                throw new UrlRecoveryException(
                    artifacts.Count == 0 ? "NUONUO_ARTIFACT_DOWNLOAD_FAILED" : "NUONUO_ARTIFACT_SELECTION_AMBIGUOUS",
                    "Invoice provider did not return one selectable invoice document.",
                    true,
                    false);
            }

            return new UrlRecoveryResult(artifacts, selectedIndex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UrlRecoveryException("URL_RECOVERY_DEADLINE_EXCEEDED", "Invoice link recovery exceeded its time limit.", true, true);
        }
        catch (PublicUrlPolicyException)
        {
            throw new UrlRecoveryException("URL_POLICY_REJECTED", "Invoice link was rejected by network policy.", false, false);
        }
        catch (HttpRequestException)
        {
            throw new UrlRecoveryException("URL_RECOVERY_WORKER_FAILED", "Invoice link could not be recovered.", true, false);
        }
        catch (JsonException)
        {
            throw new UrlRecoveryException("NUONUO_DETAIL_API_INVALID_RESPONSE", "Invoice provider returned invalid download details.", true, false);
        }
    }

    private static int? SelectPrimary(IReadOnlyList<CapturedUrlArtifact> artifacts)
    {
        var pdfIndexes = artifacts.Select((artifact, index) => (artifact, index))
            .Where(static item => item.artifact.Kind == RecoveredArtifactKind.Pdf)
            .Select(static item => item.index)
            .ToArray();
        if (pdfIndexes.Length == 1) return pdfIndexes[0];
        if (pdfIndexes.Length > 1) return null;

        var xmlIndexes = artifacts.Select((artifact, index) => (artifact, index))
            .Where(static item => item.artifact.Kind == RecoveredArtifactKind.Xml)
            .Select(static item => item.index)
            .ToArray();
        return xmlIndexes.Length == 1 ? xmlIndexes[0] : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator].Replace('+', ' '));
            var value = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..].Replace('+', ' '));
            if (!string.IsNullOrWhiteSpace(key) && !values.ContainsKey(key)) values.Add(key, value);
        }
        return values;
    }

    private static string SanitizedOrigin(Uri uri)
        => uri.IsDefaultPort ? $"{uri.Scheme}://{uri.IdnHost}" : $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
}