using System.Net;
using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class PublicUrlPolicyTests
{
    [Fact]
    public async Task Rejects_loopback_literal_and_redacts_query_from_error()
    {
        var policy = new PublicUrlPolicy();
        var act = () => policy.ValidateAsync(new Uri("http://127.0.0.1/private?token=secret"), CancellationToken.None);

        var error = await act.Should().ThrowAsync<PublicUrlPolicyException>();
        error.Which.Message.Should().NotContain("secret");
        error.Which.SafeUrl.Should().NotContain("private");
    }

    [Fact]
    public async Task Rejects_hostname_that_resolves_to_private_address()
    {
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.1.2.3")]));

        var act = () => policy.ValidateAsync(new Uri("https://invoice.example/download"), CancellationToken.None);

        var error = await act.Should().ThrowAsync<PublicUrlPolicyException>();
        error.Which.Reason.Should().Contain("globally routable");
    }

    [Fact]
    public async Task Accepts_public_dns_and_requires_connected_peer_to_match_resolution()
    {
        var publicAddress = IPAddress.Parse("203.0.114.7");
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([publicAddress]));
        var validated = await policy.ValidateAsync(new Uri("https://invoice.example/download?token=one"), CancellationToken.None);

        policy.VerifyPeerAddress(publicAddress, validated).Should().Be(publicAddress);
        var act = () => policy.VerifyPeerAddress(IPAddress.Parse("203.0.114.8"), validated);
        act.Should().Throw<PublicUrlPolicyException>();
    }

    [Fact]
    public async Task Explicit_proxy_requires_public_target_attestation_and_records_pinned_proxy_endpoint()
    {
        var publicAddress = IPAddress.Parse("93.184.216.34");
        var proxyAddress = IPAddress.Loopback;
        var attestedHosts = new List<string>();
        var policy = new PublicUrlPolicy(
            resolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("198.18.0.42")]),
            proxy: new WebProxy("http://proxy.example:7897"),
            publicResolver: (host, _) =>
            {
                attestedHosts.Add(host);
                return Task.FromResult<IReadOnlyList<IPAddress>>([publicAddress]);
            },
            proxyResolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([proxyAddress]));

        var validated = await policy.ValidateAsync(new Uri("https://invoice.example/download"), CancellationToken.None);

        validated.ResolvedAddresses.Should().ContainSingle().Which.Should().Be(publicAddress);
        validated.ProxyEndpoint.Should().NotBeNull();
        validated.ProxyEndpoint!.Host.Should().Be("proxy.example");
        validated.ProxyEndpoint.Port.Should().Be(7897);
        validated.ProxyEndpoint.ResolvedAddresses.Should().ContainSingle().Which.Should().Be(proxyAddress);
        attestedHosts.Should().Equal("invoice.example");
    }

    [Fact]
    public async Task Proxy_bypass_uses_direct_dns_rules_without_falling_back_to_attestation()
    {
        var publicAttestationCalls = 0;
        var proxy = new FixedWebProxy(new Uri("http://proxy.example:7897"), bypass: true);
        var policy = new PublicUrlPolicy(
            resolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("198.18.0.42")]),
            proxy: proxy,
            publicResolver: (_, _) =>
            {
                publicAttestationCalls++;
                return Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("93.184.216.34")]);
            },
            proxyResolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]));

        var act = () => policy.ValidateAsync(new Uri("https://invoice.example/download"), CancellationToken.None);

        var error = await act.Should().ThrowAsync<PublicUrlPolicyException>();
        error.Which.Reason.Should().Contain("globally routable");
        publicAttestationCalls.Should().Be(0);
    }

    [Fact]
    public async Task Proxy_peer_must_match_the_attested_configured_endpoint()
    {
        var proxy = new WebProxy("http://proxy.example:7897");
        var policy = new PublicUrlPolicy(
            resolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("198.18.0.42")]),
            proxy: proxy,
            publicResolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("93.184.216.34")]),
            proxyResolver: (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]));
        var validated = await policy.ValidateAsync(new Uri("https://invoice.example/download"), CancellationToken.None);

        var act = () => policy.VerifyProxyPeer(new IPEndPoint(IPAddress.Loopback, 7898), validated);

        act.Should().Throw<PublicUrlPolicyException>()
            .Which.Reason.Should().Contain("configured proxy");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@invoice.example/download")]
    [InlineData("https://invoice.example:8443/download")]
    [InlineData("http://service.localhost/download")]
    public async Task Rejects_unsupported_url_shapes(string rawUrl)
    {
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.114.7")]));

        var act = () => policy.ValidateAsync(new Uri(rawUrl), CancellationToken.None);

        await act.Should().ThrowAsync<PublicUrlPolicyException>();
    }

    private sealed class FixedWebProxy(Uri endpoint, bool bypass = false) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => bypass ? destination : endpoint;
        public bool IsBypassed(Uri host) => bypass;
    }
}
