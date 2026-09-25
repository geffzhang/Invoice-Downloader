using System.Net;
using System.Net.Sockets;
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
        using var request = new HttpRequestMessage(transportRequest.Method, url.Url);
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

    internal SocketsHttpHandler CreatePinnedHandler(ValidatedPublicUrl url)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        handler.ConnectCallback = (context, token) => ConnectPinnedAsync(context, url, token);
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