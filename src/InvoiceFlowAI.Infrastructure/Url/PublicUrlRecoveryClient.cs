using System.Net;
using System.Text.Json;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class PublicUrlRecoveryClient : IUrlRecoveryClient
{
    public const int DefaultMaxResponseBytes = 25 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private readonly PublicUrlPolicy _policy;
    private readonly IUrlRecoveryTransport _transport;
    private readonly int _maxResponseBytes;
    private readonly TimeSpan _timeout;

    public PublicUrlRecoveryClient(
        PublicUrlPolicy policy,
        IUrlRecoveryTransport transport,
        int maxResponseBytes = DefaultMaxResponseBytes,
        TimeSpan? timeout = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (maxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        _maxResponseBytes = maxResponseBytes;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        return await RecoverWithDeadlineAsync(sourceUrl, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UrlRecoveryResult> RecoverAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.ProviderFamily.Equals("nuonuo_scan_invoice", StringComparison.Ordinal))
        {
            var result = await RecoverAsync(candidate.SourceUrl, cancellationToken).ConfigureAwait(false);
            if (candidate.ProviderFamily is "chinatax_direct_invoice" or "bwjf_signed_invoice" or "fpyun_direct_invoice"
                or "pdd_direct_invoice" or "jdcloud_direct_invoice" or "kpbyd_direct_invoice"
                && !HasValidArtifactSignature(result.Content, kindScore: 3))
            {
                throw new UrlRecoveryException(
                    "DIRECT_INVOICE_NO_VALID_PDF_RECOVERED",
                    "The invoice provider did not return a valid PDF document.",
                    false,
                    false);
            }

            return result;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var shortLink = await SendFollowingRedirectsAsync(candidate.SourceUrl, HttpMethod.Get, null, deadline.Token).ConfigureAwait(false);
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
            var detailResponse = await SendFollowingRedirectsAsync(new Uri(endpoint), HttpMethod.Post, form, deadline.Token).ConfigureAwait(false);
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

            var targetUrls = new List<Uri>();
            foreach (var propertyName in new[] { "xmlUrl", "url", "ofdDownloadUrl" })
            {
                if (invoice.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var target))
                {
                    targetUrls.Add(target);
                }
            }

            if (targetUrls.Count == 0)
            {
                throw new UrlRecoveryException(
                    "NUONUO_NO_ARTIFACT_URL",
                    "Invoice provider did not return a downloadable artifact.",
                    true,
                    false);
            }

            var artifacts = new List<(UrlRecoveryResult Result, int Score)>();
            foreach (var target in targetUrls.DistinctBy(static uri => uri.AbsoluteUri, StringComparer.Ordinal))
            {
                try
                {
                    var downloaded = await SendFollowingRedirectsAsync(target, HttpMethod.Get, null, deadline.Token).ConfigureAwait(false);
                    var artifactResult = new UrlRecoveryResult(downloaded.Response.Content, downloaded.Response.ContentType);
                    var artifactScore = GetArtifactScore((artifactResult, downloaded.EffectiveUrl));
                    if (artifactScore > 0 && HasValidArtifactSignature(artifactResult.Content, artifactScore))
                    {
                        artifacts.Add((artifactResult, artifactScore));
                    }
                }
                catch (UrlRecoveryException ex) when (ex.ReasonCode is "URL_RECOVERY_HTTP_FAILED" or "URL_RECOVERY_WORKER_FAILED")
                {
                }
            }

            var pdfArtifacts = artifacts.Where(static artifact => artifact.Score == 3).ToArray();
            if (pdfArtifacts.Length == 0)
            {
                throw new UrlRecoveryException(
                    "NUONUO_ARTIFACT_DOWNLOAD_FAILED",
                    "Invoice provider did not return a valid PDF document.",
                    true,
                    false);
            }

            return pdfArtifacts[0].Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw DeadlineFailure();
        }
        catch (PublicUrlPolicyException)
        {
            throw PolicyFailure();
        }
        catch (UrlRecoveryException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw WorkerFailure();
        }
        catch (JsonException)
        {
            throw new UrlRecoveryException(
                "NUONUO_DETAIL_API_INVALID_RESPONSE",
                "Invoice provider returned invalid download details.",
                true,
                false);
        }
    }

    private async Task<UrlRecoveryResult> RecoverWithDeadlineAsync(
        Uri sourceUrl,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? formFields,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            var result = await SendFollowingRedirectsAsync(sourceUrl, method, formFields, deadline.Token).ConfigureAwait(false);
            return new UrlRecoveryResult(result.Response.Content, result.Response.ContentType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw DeadlineFailure();
        }
        catch (PublicUrlPolicyException)
        {
            throw PolicyFailure();
        }
        catch (UrlRecoveryException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw WorkerFailure();
        }
    }

    internal async Task<(UrlTransportResponse Response, Uri EffectiveUrl)> SendFollowingRedirectsAsync(
        Uri sourceUrl,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? formFields,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte>? body = null,
        string? contentType = null,
        bool allowFpyunRedirect = false)
    {
        if (allowFpyunRedirect && !IsSecureFpyunEntry(sourceUrl))
        {
            throw new PublicUrlPolicyException(PublicUrlPolicy.Sanitize(sourceUrl), "Fpyun redirect source is not approved");
        }

        var fpyunStage = 0;
        var fpyunQuery = PublicUrlPolicy.QueryPairs(sourceUrl.Query);
        var current = await _policy.ValidateAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
        for (var redirectCount = 0; ; redirectCount++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new UrlTransportRequest(current, method, formFields, body, contentType);
            var response = await _transport.SendAsync(request, _maxResponseBytes, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                if ((int)response.StatusCode is < 200 or >= 300)
                {
                    throw new UrlRecoveryException(
                        "URL_RECOVERY_HTTP_FAILED",
                        "Invoice link returned an unsuccessful response.",
                        (int)response.StatusCode >= 500,
                        false);
                }

                if (response.Content.Length > _maxResponseBytes)
                {
                    throw new UrlRecoveryException(
                        "URL_RECOVERY_RESPONSE_TOO_LARGE",
                        "Invoice link response exceeded the allowed size.",
                        false,
                        false);
                }

                return (response, current.Url);
            }

            if (redirectCount >= MaxRedirects || string.IsNullOrWhiteSpace(response.RedirectLocation))
            {
                throw new UrlRecoveryException(
                    "URL_RECOVERY_REDIRECT_LIMIT_EXCEEDED",
                    "Invoice link exceeded the redirect limit.",
                    false,
                    false);
            }

            var previous = current;
            if (allowFpyunRedirect && fpyunStage == 2)
            {
                throw new PublicUrlPolicyException(PublicUrlPolicy.Sanitize(previous.Url), "Fpyun final download must not redirect");
            }

            if (allowFpyunRedirect)
            {
                if (!Uri.TryCreate(previous.Url, response.RedirectLocation, out var redirect)
                    || !PublicUrlPolicy.QueryPairs(redirect.Query).SequenceEqual(fpyunQuery))
                {
                    throw new PublicUrlPolicyException(PublicUrlPolicy.Sanitize(previous.Url), "Fpyun redirect query changed");
                }

                if (fpyunStage == 0 && IsFpyunBaiwangFinal(redirect))
                {
                    current = await _policy.ValidateAsync(redirect, cancellationToken).ConfigureAwait(false);
                    fpyunStage = 2;
                }
                else if (fpyunStage == 0)
                {
                    current = await _policy.ValidateFpyunLegacyRedirectAsync(redirect, fpyunQuery, cancellationToken).ConfigureAwait(false);
                    fpyunStage = 1;
                }
                else
                {
                    if (!redirect.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        || !redirect.IdnHost.Equals("fp.baiwang.com", StringComparison.OrdinalIgnoreCase)
                        || redirect.Port != 80
                        || !redirect.AbsolutePath.Equals("/format/d", StringComparison.Ordinal))
                    {
                        throw new PublicUrlPolicyException(PublicUrlPolicy.Sanitize(previous.Url), "Fpyun Baiwang handoff contract mismatch");
                    }

                    var secureHandoff = new UriBuilder(redirect) { Scheme = Uri.UriSchemeHttps, Port = 443 }.Uri;
                    current = await _policy.ValidateAsync(secureHandoff, cancellationToken).ConfigureAwait(false);
                    fpyunStage = 2;
                }
            }
            else
            {
                current = await _policy.ResolveRedirectAsync(previous, response.RedirectLocation, cancellationToken).ConfigureAwait(false);
            }
            if ((formFields is { Count: > 0 } || body is not null) && !IsSameOrigin(previous.Url, current.Url))
            {
                throw new UrlRecoveryException(
                    "URL_RECOVERY_CROSS_ORIGIN_POST_REDIRECT_BLOCKED",
                    "Invoice provider redirected a protected request to another origin.",
                    false,
                    false);
            }

            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.MovedPermanently)
            {
                method = HttpMethod.Get;
                formFields = null;
                body = null;
                contentType = null;
            }
        }
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

    internal static int GetArtifactScore((UrlRecoveryResult Result, Uri EffectiveUrl) downloaded)
    {
        var type = downloaded.Result.ContentType;
        var extension = Path.GetExtension(downloaded.EffectiveUrl.AbsolutePath);
        if (type.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return 3;
        if (type.Contains("xml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return 2;
        if (type.Contains("ofd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ofd", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    internal static bool HasValidArtifactSignature(ReadOnlyMemory<byte> content, int kindScore)
    {
        var bytes = content.Span;
        return kindScore switch
        {
            3 => bytes.StartsWith("%PDF-"u8),
            2 => LooksLikeXml(bytes),
            1 => bytes.StartsWith("PK\u0003\u0004"u8),
            _ => false,
        };
    }

    private static bool LooksLikeXml(ReadOnlySpan<byte> content)
    {
        var prefix = content[..Math.Min(content.Length, 256)];
        var text = System.Text.Encoding.UTF8.GetString(prefix).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith('<');
    }

    private static int KindScore(string kind) => kind.ToLowerInvariant() switch { "pdf" => 3, "xml" => 2, "ofd" => 1, _ => 0 };

    private static bool IsSameOrigin(Uri left, Uri right)
        => left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
            && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;

    private static bool IsSecureFpyunEntry(Uri uri)
        => uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IdnHost.Equals("sdapi.fpyun.com.cn", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443
            && uri.AbsolutePath.Equals("/invoice/qd/download/getInvoiceFile", StringComparison.Ordinal)
            && PublicUrlPolicy.QueryPairs(uri.Query).Any(static pair => pair.Key == "fptqm" && pair.Value.Length > 0);

    private static bool IsFpyunBaiwangFinal(Uri uri)
        => uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IdnHost.Equals("fp.baiwang.com", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443
            && uri.AbsolutePath.Equals("/format/d", StringComparison.Ordinal);

    private static UrlRecoveryException DeadlineFailure()
        => new("URL_RECOVERY_DEADLINE_EXCEEDED", "Invoice link recovery exceeded its time limit.", true, true);

    private static UrlRecoveryException WorkerFailure()
        => new("URL_RECOVERY_WORKER_FAILED", "Invoice link could not be recovered.", true, false);

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static UrlRecoveryException PolicyFailure()
        => new("URL_POLICY_REJECTED", "Invoice link was rejected by network policy.", false, false);
}