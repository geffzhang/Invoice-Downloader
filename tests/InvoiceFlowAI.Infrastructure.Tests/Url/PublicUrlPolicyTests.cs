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
}
