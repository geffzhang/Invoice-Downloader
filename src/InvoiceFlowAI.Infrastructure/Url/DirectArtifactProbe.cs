using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class DirectArtifactProbe
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    private readonly PublicUrlRecoveryClient _client;
    private readonly int _maxAttempts;
    private readonly int _maxArchiveMembers;
    private readonly long _maxArchiveTotalBytes;
    private readonly long _maxArchiveMemberBytes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public DirectArtifactProbe(
        PublicUrlRecoveryClient client,
        int maxAttempts = 3,
        int maxArchiveMembers = 128,
        long maxArchiveTotalBytes = 64 * 1024 * 1024,
        long maxArchiveMemberBytes = 25 * 1024 * 1024,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (maxArchiveMembers < 1) throw new ArgumentOutOfRangeException(nameof(maxArchiveMembers));
        if (maxArchiveTotalBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxArchiveTotalBytes));
        if (maxArchiveMemberBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxArchiveMemberBytes));
        _maxAttempts = maxAttempts;
        _maxArchiveMembers = maxArchiveMembers;
        _maxArchiveTotalBytes = maxArchiveTotalBytes;
        _maxArchiveMemberBytes = maxArchiveMemberBytes;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
    }

    public async Task<IReadOnlyList<CapturedUrlArtifact>> ProbeAsync(
        Uri sourceUrl,
        int sourceUrlOrdinal,
        CancellationToken cancellationToken,
        bool allowFpyunRedirect = false,
        string? expectedInvoiceNumber = null)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        if (sourceUrlOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(sourceUrlOrdinal));

        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var downloaded = await _client.SendFollowingRedirectsAsync(
                    sourceUrl, HttpMethod.Get, null, cancellationToken,
                    allowFpyunRedirect: allowFpyunRedirect).ConfigureAwait(false);
                var artifacts = Capture(downloaded.Response, downloaded.EffectiveUrl, sourceUrl, sourceUrlOrdinal, expectedInvoiceNumber);
                if (artifacts.Count > 0) return artifacts;
            }
            catch (PublicUrlPolicyException)
            {
                throw new UrlRecoveryException("URL_POLICY_REJECTED", "Invoice link was rejected by network policy.", false, false);
            }
            catch (UrlRecoveryException exception) when (exception.ReasonCode == "URL_POLICY_REJECTED")
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UrlRecoveryException exception) when (!exception.Retryable)
            {
                return [];
            }
            catch (UrlRecoveryException exception) when (exception.Retryable && attempt + 1 < _maxAttempts)
            {
            }
            catch (UrlRecoveryException)
            {
                return [];
            }
            catch (HttpRequestException) when (attempt + 1 < _maxAttempts)
            {
            }
            catch (HttpRequestException)
            {
                return [];
            }

            if (attempt + 1 < _maxAttempts)
            {
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return [];
    }

    private IReadOnlyList<CapturedUrlArtifact> Capture(
        UrlTransportResponse response,
        Uri resolvedUrl,
        Uri sourceUrl,
        int sourceOrdinal,
        string? expectedInvoiceNumber)
    {
        if (response.Content.Span.StartsWith("PK\u0003\u0004"u8))
        {
            return CaptureArchive(response.Content, resolvedUrl, sourceUrl, sourceOrdinal, expectedInvoiceNumber);
        }

        var kind = InferKind(response.ContentType, resolvedUrl);
        if (kind is null || !PublicUrlRecoveryClient.HasValidArtifactSignature(response.Content, Score(kind.Value))) return [];
        return [MarkResolvedUrlMatch(
            CreateArtifact(kind.Value, response.Content, response.ContentType, resolvedUrl, sourceOrdinal),
            resolvedUrl,
            expectedInvoiceNumber)];
    }

    private IReadOnlyList<CapturedUrlArtifact> CaptureArchive(
        ReadOnlyMemory<byte> payload,
        Uri resolvedUrl,
        Uri sourceUrl,
        int sourceOrdinal,
        string? expectedInvoiceNumber)
    {
        try
        {
            using var input = new MemoryStream(payload.ToArray(), writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var members = archive.Entries.Where(static entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
            if (members.Length > _maxArchiveMembers
                || members.Aggregate(0L, (size, entry) => checked(size + entry.Length)) > _maxArchiveTotalBytes)
            {
                return [];
            }

            var artifacts = new List<CapturedUrlArtifact>();
            foreach (var member in members)
            {
                var kind = InferKind(string.Empty, new Uri("https://archive.invalid/" + Uri.EscapeDataString(member.Name)));
                if (kind is null) continue;
                if (member.Length > _maxArchiveMemberBytes) return [];
                using var memberStream = member.Open();
                using var output = new MemoryStream((int)Math.Min(member.Length, int.MaxValue));
                var buffer = new byte[8192];
                int read;
                while ((read = memberStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > _maxArchiveMemberBytes) return [];
                    output.Write(buffer, 0, read);
                }

                var content = output.ToArray();
                if (!PublicUrlRecoveryClient.HasValidArtifactSignature(content, Score(kind.Value))) continue;
                artifacts.Add(MarkResolvedUrlMatch(
                    CreateArtifact(kind.Value, content, ContentType(kind.Value), resolvedUrl, sourceOrdinal),
                    resolvedUrl,
                    expectedInvoiceNumber));
            }

            return artifacts;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            return [];
        }
    }

    private static CapturedUrlArtifact CreateArtifact(
        RecoveredArtifactKind kind,
        ReadOnlyMemory<byte> content,
        string contentType,
        Uri resolvedUrl,
        int sourceOrdinal)
        => new(kind, contentType, content, sourceOrdinal,
            Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant(),
            resolvedUrl.IsDefaultPort ? $"{resolvedUrl.Scheme}://{resolvedUrl.IdnHost}" : $"{resolvedUrl.Scheme}://{resolvedUrl.IdnHost}:{resolvedUrl.Port}",
            kind == RecoveredArtifactKind.Xml ? ParseXmlFields(content) : new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            "DIRECT_ARTIFACT_CAPTURED");

    private static CapturedUrlArtifact MarkResolvedUrlMatch(
        CapturedUrlArtifact artifact,
        Uri resolvedUrl,
        string? expectedInvoiceNumber)
        => !string.IsNullOrWhiteSpace(expectedInvoiceNumber)
            && resolvedUrl.AbsoluteUri.Contains(expectedInvoiceNumber, StringComparison.Ordinal)
                ? artifact with { ExpectedMatch = true, MatchReasonCode = "invoice_number_from_url" }
                : artifact;

    private static IReadOnlyDictionary<string, string> ParseXmlFields(ReadOnlyMemory<byte> content)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = Math.Min(content.Length * 2L, 4 * 1024 * 1024),
                IgnoreComments = true,
            });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var key = reader.LocalName.ToLowerInvariant() switch
                {
                    "invoicenumber" or "eiid" or "fpdm" or "number" => "invoice_number",
                    "invoicecode" => "invoice_code",
                    "sellername" or "seller" or "xfmc" => "seller",
                    "buyername" => "purchaser",
                    "invoicedate" or "issuetime" or "issuedate" or "requesttime" or "kprq" => "invoice_date",
                    "totaltax-includedamount" or "totaltaxincludedamount" => "amount",
                    _ => null,
                };
                if (key is null || reader.IsEmptyElement) continue;
                var value = reader.ReadElementContentAsString().Trim();
                if (value.Length > 0) fields.TryAdd(key, value);
            }
        }
        catch (XmlException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return fields;
    }

    private static RecoveredArtifactKind? InferKind(string contentType, Uri url)
    {
        var extension = Path.GetExtension(url.AbsolutePath);
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return RecoveredArtifactKind.Pdf;
        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return RecoveredArtifactKind.Xml;
        if (contentType.Contains("ofd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ofd", StringComparison.OrdinalIgnoreCase)) return RecoveredArtifactKind.Ofd;
        return null;
    }

    private static int Score(RecoveredArtifactKind kind) => kind switch
    {
        RecoveredArtifactKind.Pdf => 3,
        RecoveredArtifactKind.Xml => 2,
        RecoveredArtifactKind.Ofd => 1,
        _ => 0,
    };

    private static string ContentType(RecoveredArtifactKind kind) => kind switch
    {
        RecoveredArtifactKind.Pdf => "application/pdf",
        RecoveredArtifactKind.Xml => "application/xml",
        RecoveredArtifactKind.Ofd => "application/ofd",
        _ => "application/octet-stream",
    };
}