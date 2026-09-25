using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class PublicUrlPolicyException : Exception
{
    public PublicUrlPolicyException(string safeUrl, string reason)
        : base($"URL_POLICY_REJECTED: {reason}; url={safeUrl}")
    {
        SafeUrl = safeUrl;
        Reason = reason;
    }

    public string SafeUrl { get; }
    public string Reason { get; }
}

public sealed record ValidatedPublicUrl(
    Uri Url,
    string Host,
    int Port,
    IReadOnlyList<IPAddress> ResolvedAddresses,
    ValidatedProxyEndpoint? ProxyEndpoint = null);

public sealed record ValidatedProxyEndpoint(
    Uri Uri,
    string Host,
    int Port,
    IReadOnlyList<IPAddress> ResolvedAddresses);

public sealed class PublicUrlPolicy
{
    private static readonly IReadOnlyDictionary<string, int> DefaultPorts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Uri.UriSchemeHttp] = 80,
        [Uri.UriSchemeHttps] = 443,
    };

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> _resolver;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> _publicResolver;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> _proxyResolver;
    private readonly HashSet<(string Host, int Port)> _allowedHttpsPorts;
    private readonly IWebProxy? _proxy;

    public PublicUrlPolicy(
        Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? resolver = null,
        IEnumerable<(string Host, int Port)>? allowedHttpsPorts = null,
        IWebProxy? proxy = null,
        Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? publicResolver = null,
        Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? proxyResolver = null)
    {
        _resolver = resolver ?? ResolveHostAsync;
        _publicResolver = publicResolver ?? PublicDnsOverHttpsResolver.ResolveAsync;
        _proxyResolver = proxyResolver ?? _resolver;
        _proxy = proxy;
        _allowedHttpsPorts = (allowedHttpsPorts ?? Array.Empty<(string Host, int Port)>())
            .Select(entry => (CanonicalizeHost(entry.Host), entry.Port))
            .ToHashSet();
    }

    public async Task<ValidatedPublicUrl> ValidateAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();

        var safeUrl = Sanitize(uri);
        var scheme = uri.Scheme.ToLowerInvariant();
        if (!DefaultPorts.TryGetValue(scheme, out var defaultPort))
        {
            throw Reject(safeUrl, "scheme must be http or https");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Reject(safeUrl, "credentials are not allowed");
        }

        string host;
        try
        {
            host = CanonicalizeHost(uri.IdnHost);
        }
        catch (ArgumentException)
        {
            throw Reject(safeUrl, "hostname is invalid");
        }

        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            throw Reject(safeUrl, "localhost is not allowed");
        }

        var port = uri.Port;
        if (port != defaultPort && (scheme != Uri.UriSchemeHttps || !_allowedHttpsPorts.Contains((host, port))))
        {
            throw Reject(safeUrl, "port is not allowed");
        }

        var proxyEndpoint = await ResolveProxyEndpointAsync(uri, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<IPAddress> resolved;
        if (IPAddress.TryParse(host, out var literal))
        {
            resolved = [NormalizeAddress(literal)];
        }
        else
        {
            try
            {
                resolved = await _resolver(host, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (proxyEndpoint is not null)
            {
                resolved = Array.Empty<IPAddress>();
            }
            catch
            {
                throw Reject(safeUrl, "hostname resolution failed");
            }
        }

        if (resolved.Count == 0)
        {
            if (proxyEndpoint is null)
            {
                throw Reject(safeUrl, "hostname did not resolve");
            }
        }

        var normalizedAddresses = resolved.Select(NormalizeAddress).Distinct().ToArray();
        if (proxyEndpoint is not null && !IPAddress.TryParse(host, out _))
        {
            try
            {
                normalizedAddresses = (await _publicResolver(host, cancellationToken).ConfigureAwait(false))
                    .Select(NormalizeAddress)
                    .Distinct()
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw Reject(safeUrl, "public DNS attestation unavailable");
            }

            if (normalizedAddresses.Length == 0)
            {
                throw Reject(safeUrl, "public DNS attestation returned no addresses");
            }
        }

        if (normalizedAddresses.Any(address => !IsPublicUnicast(address)))
        {
            throw Reject(safeUrl, proxyEndpoint is null
                ? "destination is not globally routable"
                : "public DNS attestation returned a non-public address");
        }

        var normalizedUrl = new UriBuilder(uri)
        {
            Scheme = scheme,
            Host = host,
            Fragment = string.Empty,
        }.Uri;

        return new ValidatedPublicUrl(normalizedUrl, host, port, normalizedAddresses, proxyEndpoint);
    }

    public Task<ValidatedPublicUrl> ResolveRedirectAsync(
        ValidatedPublicUrl current,
        string location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!Uri.TryCreate(current.Url, location, out var redirect))
        {
            throw Reject(Sanitize(current.Url), "redirect URL could not be parsed");
        }

        return ValidateAsync(redirect, cancellationToken);
    }

    internal Task<ValidatedPublicUrl> ValidateFpyunLegacyRedirectAsync(
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> expectedQuery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 7100
            || !uri.AbsolutePath.Equals("/qd/download/getInvoiceFile", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !IPAddress.TryParse(uri.Host, out var address)
            || !QueryPairs(uri.Query).SequenceEqual(expectedQuery))
        {
            throw Reject(Sanitize(uri), "Fpyun redirect contract mismatch");
        }

        var normalized = NormalizeAddress(address);
        if (!IsPublicUnicast(normalized))
        {
            throw Reject(Sanitize(uri), "Fpyun redirect destination is not globally routable");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ValidatedPublicUrl(uri, normalized.ToString(), 7100, [normalized]));
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> QueryPairs(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair =>
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
                return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(key.Replace('+', ' ')),
                    Uri.UnescapeDataString(value.Replace('+', ' ')));
            })
            .ToArray();

    public IPAddress VerifyPeerAddress(IPAddress peerAddress, ValidatedPublicUrl validated)
    {
        ArgumentNullException.ThrowIfNull(peerAddress);
        ArgumentNullException.ThrowIfNull(validated);
        var normalizedPeer = NormalizeAddress(peerAddress);
        if (!IsPublicUnicast(normalizedPeer) || !validated.ResolvedAddresses.Contains(normalizedPeer))
        {
            throw Reject(Sanitize(validated.Url), "connected peer does not match the validated public destination");
        }

        return normalizedPeer;
    }

    public IPAddress VerifyProxyPeer(IPEndPoint peer, ValidatedPublicUrl validated)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(validated);
        var proxy = validated.ProxyEndpoint;
        var normalizedPeer = NormalizeAddress(peer.Address);
        if (proxy is null
            || peer.Port != proxy.Port
            || !proxy.ResolvedAddresses.Contains(normalizedPeer))
        {
            throw Reject(Sanitize(validated.Url), "connected peer is not the configured proxy endpoint");
        }

        return normalizedPeer;
    }

    private async Task<ValidatedProxyEndpoint?> ResolveProxyEndpointAsync(Uri target, CancellationToken cancellationToken)
    {
        if (_proxy is null) return null;

        Uri? endpoint;
        try
        {
            if (_proxy.IsBypassed(target)) return null;
            endpoint = _proxy.GetProxy(target);
        }
        catch
        {
            return null;
        }

        if (endpoint is null) return null;
        if (endpoint == target) return null;
        if ((!endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw Reject(Sanitize(target), "configured proxy endpoint is invalid");
        }

        var proxyHost = CanonicalizeHost(endpoint.IdnHost);
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(proxyHost, out var literal))
        {
            addresses = [NormalizeAddress(literal)];
        }
        else
        {
            try
            {
                addresses = await _proxyResolver(proxyHost, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw Reject(Sanitize(target), "configured proxy endpoint did not resolve");
            }
        }

        var normalizedAddresses = addresses.Select(NormalizeAddress).Distinct().ToArray();
        if (normalizedAddresses.Length == 0)
        {
            throw Reject(Sanitize(target), "configured proxy endpoint did not resolve");
        }

        return new ValidatedProxyEndpoint(endpoint, proxyHost, endpoint.Port, normalizedAddresses);
    }

    public static string Sanitize(Uri? uri)
    {
        if (uri is null || string.IsNullOrWhiteSpace(uri.Host))
        {
            return "<invalid-url>";
        }

        try
        {
            var scheme = uri.Scheme.ToLowerInvariant();
            var host = CanonicalizeHost(uri.IdnHost);
            var displayHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
            var defaultPort = DefaultPorts.TryGetValue(scheme, out var port) ? port : -1;
            var explicitPort = uri.IsDefaultPort || uri.Port == defaultPort ? string.Empty : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";
            return $"{scheme}://{displayHost}{explicitPort}/<redacted>";
        }
        catch
        {
            return "<invalid-url>";
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveHostAsync(string host, CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

    private static string CanonicalizeHost(string host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Hostname is empty.", nameof(host));
        if (IPAddress.TryParse(value, out var address)) return NormalizeAddress(address).ToString().ToLowerInvariant();
        return new IdnMapping().GetAscii(value).ToLowerInvariant();
    }

    private static IPAddress NormalizeAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsPublicUnicast(IPAddress rawAddress)
    {
        var address = NormalizeAddress(rawAddress);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var first = bytes[0];
            var second = bytes[1];
            var third = bytes[2];
            return first != 0
                && first != 10
                && first != 127
                && !(first == 100 && second is >= 64 and <= 127)
                && !(first == 169 && second == 254)
                && !(first == 172 && second is >= 16 and <= 31)
                && !(first == 192 && (second == 168 || (second == 0 && third == 0) || (second == 0 && third == 2) || (second == 88 && third == 99)))
                && !(first == 198 && (second is 18 or 19 || (second == 51 && third == 100)))
                && !(first == 203 && second == 0 && third == 113)
                && first < 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var ipv6 = address.GetAddressBytes();
        var globallyRoutable = (ipv6[0] & 0xE0) == 0x20;
        var documentationRange = ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0D && ipv6[3] == 0xB8;
        var orchidV1Range = ipv6[0] == 0x20 && ipv6[1] == 0x01 && (ipv6[2] & 0xF0) == 0x10;
        return globallyRoutable && !documentationRange && !orchidV1Range;
    }

    private static PublicUrlPolicyException Reject(string safeUrl, string reason)
        => new(safeUrl, reason);
}