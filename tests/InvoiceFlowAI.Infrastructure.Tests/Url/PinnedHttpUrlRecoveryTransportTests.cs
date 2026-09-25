using System.Net;
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

    private static ValidatedPublicUrl ValidatedUrl()
        => new(new Uri("https://example.com/invoice"), "example.com", 443, [IPAddress.Parse("8.8.8.8")]);

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