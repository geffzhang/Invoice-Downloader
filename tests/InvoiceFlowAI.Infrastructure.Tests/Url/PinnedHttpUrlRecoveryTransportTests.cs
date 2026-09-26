using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class PinnedHttpUrlRecoveryTransportTests
{
    [Fact]
    public void Pinned_handler_bypasses_system_proxy_and_automatic_redirects()
    {
        var transport = new PinnedHttpUrlRecoveryTransport(new PublicUrlPolicy());
        using var handler = transport.CreatePinnedHandler(ValidatedUrl());

        handler.UseProxy.Should().BeFalse();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public async Task Failed_response_read_disposes_stream_response_and_handler()
    {
        var stream = new FailingReadStream();
        var handler = new TrackingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        var transport = new PinnedHttpUrlRecoveryTransport(new PublicUrlPolicy(), () => handler);

        var act = () => transport.SendAsync(
            new UrlTransportRequest(ValidatedUrl(), HttpMethod.Get), 1024, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        stream.Disposed.Should().BeTrue();
        handler.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Https_proxy_connect_targets_attested_ip_instead_of_origin_hostname()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestLine = string.Empty;
        var proxyTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            requestLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? string.Empty;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))) { }
            await stream.WriteAsync("HTTP/1.1 502 Synthetic Stop\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        });
        var validated = ProxyValidatedUrl("https://invoice.example/invoice", proxyPort);
        var transport = new PinnedHttpUrlRecoveryTransport(new PublicUrlPolicy());
        using var handler = transport.CreatePinnedHandler(validated);
        using var client = new HttpClient(handler);

        var act = () => client.GetAsync(validated.Url);

        await act.Should().ThrowAsync<HttpRequestException>();
        await proxyTask;
        requestLine.Should().Be("CONNECT 93.184.216.34:443 HTTP/1.1");
    }

    [Fact]
    public async Task Https_proxy_tls_uses_configured_proxy_hostname_as_server_identity()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=unrelated.invalid",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
        var serverName = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxyTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var stream = new SslStream(client.GetStream());
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, requestedName) =>
                    {
                        serverName.TrySetResult(requestedName);
                        return certificate;
                    },
                });
            }
            catch (AuthenticationException)
            {
            }
            catch (IOException)
            {
            }
        });
        var validated = HttpsProxyValidatedUrl("https://invoice.example/invoice", proxyPort);
        var transport = new PinnedHttpUrlRecoveryTransport(new PublicUrlPolicy());

        var act = () => transport.SendAsync(new UrlTransportRequest(validated, HttpMethod.Get), 1024, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        (await serverName.Task.WaitAsync(TimeSpan.FromSeconds(3))).Should().Be("proxy.example");
        await proxyTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Proxy_bypass_uses_direct_policy_and_disables_transport_proxy()
    {
        var proxy = new FixedWebProxy(new Uri("http://proxy.example:7897"), bypass: true);
        var policy = new PublicUrlPolicy(
            resolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("93.184.216.34")]),
            proxy: proxy,
            publicResolver: (_, _) => throw new InvalidOperationException("bypassed target must not use public attestation"),
            proxyResolver: (_, _) => throw new InvalidOperationException("bypassed target must not resolve a proxy"));

        var validated = await policy.ValidateAsync(new Uri("https://invoice.example/invoice"), CancellationToken.None);
        var transport = new PinnedHttpUrlRecoveryTransport(policy);
        using var handler = transport.CreatePinnedHandler(validated);

        validated.ProxyEndpoint.Should().BeNull();
        handler.UseProxy.Should().BeFalse();
        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public async Task Http_proxy_request_uses_attested_ip_and_preserves_origin_host_header()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestLine = string.Empty;
        var hostHeader = string.Empty;
        var proxyTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            requestLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? string.Empty;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3))))
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) hostHeader = line[5..].Trim();
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Type: application/pdf\r\nConnection: close\r\n\r\nPDF"u8.ToArray());
        });
        var validated = ProxyValidatedUrl("http://invoice.example/invoice", proxyPort);
        var transport = new PinnedHttpUrlRecoveryTransport(new PublicUrlPolicy());

        var response = await transport.SendAsync(new UrlTransportRequest(validated, HttpMethod.Get), 32, CancellationToken.None);

        await proxyTask;
        requestLine.Should().Be("GET http://93.184.216.34/invoice HTTP/1.1");
        hostHeader.Should().Be("invoice.example");
        response.Content.ToArray().Should().Equal("PDF"u8.ToArray());
    }

    private static ValidatedPublicUrl ValidatedUrl()
        => new(new Uri("https://example.com/invoice"), "example.com", 443, [IPAddress.Parse("8.8.8.8")]);

    private static ValidatedPublicUrl ProxyValidatedUrl(string rawUrl, int proxyPort)
    {
        var uri = new Uri(rawUrl);
        var endpointUri = new Uri($"http://127.0.0.1:{proxyPort}");
        var endpoint = new ValidatedProxyEndpoint(endpointUri, "127.0.0.1", proxyPort, [IPAddress.Loopback]);
        return new ValidatedPublicUrl(uri, "invoice.example", uri.Port, [IPAddress.Parse("93.184.216.34")], endpoint);
    }

    private static ValidatedPublicUrl HttpsProxyValidatedUrl(string rawUrl, int proxyPort)
    {
        var uri = new Uri(rawUrl);
        var endpointUri = new Uri($"https://proxy.example:{proxyPort}");
        var endpoint = new ValidatedProxyEndpoint(endpointUri, "proxy.example", proxyPort, [IPAddress.Loopback]);
        return new ValidatedPublicUrl(uri, "invoice.example", uri.Port, [IPAddress.Parse("93.184.216.34")], endpoint);
    }

    private sealed class FixedWebProxy(Uri endpoint, bool bypass = false) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => bypass ? destination : endpoint;
        public bool IsBypassed(Uri host) => bypass;
    }

    private sealed class TrackingHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory());

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("synthetic read failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("synthetic read failure"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}