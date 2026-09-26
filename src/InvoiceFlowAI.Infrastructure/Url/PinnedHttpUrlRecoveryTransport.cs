using System.Net;
using System.Security.Authentication;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Net.Http;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class PinnedHttpUrlRecoveryTransport : IUrlRecoveryTransport
{
    private readonly PublicUrlPolicy _policy;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public PinnedHttpUrlRecoveryTransport(PublicUrlPolicy policy)
        => _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    internal PinnedHttpUrlRecoveryTransport(PublicUrlPolicy policy, Func<HttpMessageHandler> handlerFactory)
        : this(policy)
        => _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));

    public async Task<UrlTransportResponse> SendAsync(
        UrlTransportRequest transportRequest,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transportRequest);
        var url = transportRequest.Url;
        using var handler = _handlerFactory?.Invoke() ?? CreatePinnedHandler(url);

        using var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var requestUrl = GetRequestUri(url);
        using var request = new HttpRequestMessage(transportRequest.Method, requestUrl);
        if (url.ProxyEndpoint is not null && url.Url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Host = FormatHostHeader(url);
        }
        if (transportRequest.FormFields is { Count: > 0 } && transportRequest.Body is not null)
        {
            throw new ArgumentException("A URL recovery request cannot contain both form fields and a raw body.", nameof(transportRequest));
        }
        if (transportRequest.FormFields is { Count: > 0 })
        {
            request.Content = new FormUrlEncodedContent(transportRequest.FormFields);
        }
        else if (transportRequest.Body is { } body)
        {
            request.Content = new ByteArrayContent(body.ToArray());
            if (!string.IsNullOrWhiteSpace(transportRequest.ContentType))
            {
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(transportRequest.ContentType);
            }
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        var redirectLocation = response.Headers.Location?.ToString();
        if (IsRedirect(response.StatusCode))
        {
            return new UrlTransportResponse(response.StatusCode, ReadOnlyMemory<byte>.Empty, string.Empty, redirectLocation);
        }

        if (response.Content.Headers.ContentLength is > 0 and var contentLength && contentLength > maxResponseBytes)
        {
            throw TooLarge();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maxResponseBytes, 64 * 1024));
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) break;
            if (buffer.Length + bytesRead > maxResponseBytes) throw TooLarge();
            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }

        return new UrlTransportResponse(
            response.StatusCode,
            buffer.ToArray(),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            redirectLocation);
    }

    private async ValueTask<Stream> ConnectPinnedAsync(
        SocketsHttpConnectionContext context,
        ValidatedPublicUrl validated,
        CancellationToken cancellationToken)
    {
        var requestedHost = context.DnsEndPoint.Host.Trim('[', ']').TrimEnd('.');
        if (!requestedHost.Equals(validated.Host, StringComparison.OrdinalIgnoreCase)
            || context.DnsEndPoint.Port != validated.Port)
        {
            throw new HttpRequestException("The connection endpoint did not match the validated destination.");
        }

        Exception? lastFailure = null;
        foreach (var address in validated.ResolvedAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, validated.Port), cancellationToken).ConfigureAwait(false);
                if (socket.RemoteEndPoint is not IPEndPoint peer)
                {
                    throw new HttpRequestException("The connected peer could not be verified.");
                }

                _policy.VerifyPeerAddress(peer.Address, validated);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (PublicUrlPolicyException)
            {
                socket.Dispose();
                throw new HttpRequestException("The connected peer did not match the validated destination.");
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch (HttpRequestException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
        }

        throw new HttpRequestException("Could not connect to a validated public address.", lastFailure);
    }

    private async ValueTask<Stream> ConnectProxySocketAsync(
        SocketsHttpConnectionContext context,
        ValidatedPublicUrl validated,
        CancellationToken cancellationToken)
    {
        var proxy = validated.ProxyEndpoint
            ?? throw new HttpRequestException("The configured proxy endpoint is unavailable.");
        if (!Canonicalize(context.DnsEndPoint.Host).Equals(proxy.Host, StringComparison.OrdinalIgnoreCase)
            || context.DnsEndPoint.Port != proxy.Port)
        {
            throw new HttpRequestException("The connection endpoint did not match the configured proxy.");
        }

        Exception? lastFailure = null;
        foreach (var address in proxy.ResolvedAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, proxy.Port), cancellationToken).ConfigureAwait(false);
                if (socket.RemoteEndPoint is not IPEndPoint peer)
                {
                    throw new HttpRequestException("The connected proxy peer could not be verified.");
                }

                _policy.VerifyProxyPeer(peer, validated);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (PublicUrlPolicyException)
            {
                socket.Dispose();
                throw new HttpRequestException("The connected peer did not match the configured proxy.");
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException)
            {
                socket.Dispose();
                lastFailure = ex;
            }
        }

        throw new HttpRequestException("Could not connect to the configured proxy endpoint.", lastFailure);
    }

    private async ValueTask<Stream> ConnectThroughProxyAsync(
        SocketsHttpConnectionContext context,
        ValidatedPublicUrl validated,
        CancellationToken cancellationToken)
    {
        var proxy = validated.ProxyEndpoint
            ?? throw new HttpRequestException("The configured proxy endpoint is unavailable.");
        if (!Canonicalize(context.DnsEndPoint.Host).Equals(validated.Host, StringComparison.OrdinalIgnoreCase)
            || context.DnsEndPoint.Port != validated.Port)
        {
            throw new HttpRequestException("The connection endpoint did not match the validated destination.");
        }

        Exception? lastFailure = null;
        foreach (var originAddress in validated.ResolvedAddresses)
        {
            foreach (var proxyAddress in proxy.ResolvedAddresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var socket = new Socket(proxyAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                Stream? stream = null;
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(proxyAddress, proxy.Port), cancellationToken).ConfigureAwait(false);
                    if (socket.RemoteEndPoint is not IPEndPoint peer)
                    {
                        throw new HttpRequestException("The connected proxy peer could not be verified.");
                    }

                    _policy.VerifyProxyPeer(peer, validated);
                    stream = new NetworkStream(socket, ownsSocket: true);
                    if (proxy.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        var secureProxyStream = new SslStream(stream, leaveInnerStreamOpen: false);
                        await secureProxyStream.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions { TargetHost = proxy.Host },
                            cancellationToken).ConfigureAwait(false);
                        stream = secureProxyStream;
                    }

                    var authority = FormatAuthority(originAddress, validated.Port);
                    var connectRequest = Encoding.ASCII.GetBytes(
                        $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: Keep-Alive\r\n\r\n");
                    await stream.WriteAsync(connectRequest, cancellationToken).ConfigureAwait(false);
                    await EnsureConnectAcceptedAsync(stream, cancellationToken).ConfigureAwait(false);
                    return stream;
                }
                catch (OperationCanceledException)
                {
                    if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                    else socket.Dispose();
                    throw;
                }
                catch (PublicUrlPolicyException)
                {
                    if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                    else socket.Dispose();
                    throw new HttpRequestException("The connected peer did not match the configured proxy.");
                }
                catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or HttpRequestException)
                {
                    if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                    else socket.Dispose();
                    lastFailure = ex;
                }
            }
        }

        throw new HttpRequestException("Could not establish a tunnel through the configured proxy.", lastFailure);
    }

    private static async Task EnsureConnectAcceptedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var oneByte = new byte[1];
        while (header.Length < 8192)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The proxy closed the CONNECT response.");
            header.WriteByte(oneByte[0]);
            var length = header.Length;
            if (length >= 4
                && header.GetBuffer()[length - 4] == '\r'
                && header.GetBuffer()[length - 3] == '\n'
                && header.GetBuffer()[length - 2] == '\r'
                && header.GetBuffer()[length - 1] == '\n')
            {
                var statusLine = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)length)
                    .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (statusLine is null
                    || !statusLine.StartsWith("HTTP/", StringComparison.Ordinal)
                    || statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) != "200")
                {
                    throw new HttpRequestException("The configured proxy rejected the CONNECT request.");
                }

                return;
            }
        }

        throw new IOException("The proxy CONNECT response exceeded the header limit.");
    }

    private static Uri GetRequestUri(ValidatedPublicUrl url)
    {
        if (url.ProxyEndpoint is null || !url.Url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return url.Url;
        }

        return new UriBuilder(url.Url) { Host = url.ResolvedAddresses[0].ToString(), Port = url.Port }.Uri;
    }

    private static string FormatHostHeader(ValidatedPublicUrl url)
    {
        var host = url.Host.Contains(':', StringComparison.Ordinal) ? $"[{url.Host}]" : url.Host;
        return url.Url.IsDefaultPort ? host : $"{host}:{url.Port}";
    }

    private static string FormatAuthority(IPAddress address, int port)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"{host}:{port}";
    }

    private static string Canonicalize(string host)
        => IPAddress.TryParse(host.Trim('[', ']'), out var address)
            ? address.ToString()
            : new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant();

    internal SocketsHttpHandler CreatePinnedHandler(ValidatedPublicUrl url)
    {
        var useHttpProxy = url.ProxyEndpoint is not null
            && url.Url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = useHttpProxy,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (useHttpProxy)
        {
            handler.Proxy = new WebProxy(url.ProxyEndpoint!.Uri);
            handler.ConnectCallback = (context, token) => ConnectProxySocketAsync(context, url, token);
        }
        else if (url.ProxyEndpoint is not null)
        {
            handler.ConnectCallback = (context, token) => ConnectThroughProxyAsync(context, url, token);
        }
        else
        {
            handler.ConnectCallback = (context, token) => ConnectPinnedAsync(context, url, token);
        }

        return handler;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static UrlRecoveryException TooLarge()
        => new("URL_RECOVERY_RESPONSE_TOO_LARGE", "Invoice link response exceeded the allowed size.", false, false);
}