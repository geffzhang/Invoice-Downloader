using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class DirectArtifactProbeTests
{
    [Fact]
    public async Task Retries_transient_http_failures_and_returns_signature_valid_artifact()
    {
        var payload = Encoding.ASCII.GetBytes("%PDF-1.7 fixture");
        var transport = new FakeTransport(
            Response(HttpStatusCode.ServiceUnavailable, [], "text/plain"),
            Response(HttpStatusCode.ServiceUnavailable, [], "text/plain"),
            Response(HttpStatusCode.OK, payload, "application/pdf"));
        var probe = NewProbe(transport, maxAttempts: 3);

        var artifacts = await probe.ProbeAsync(new Uri("https://files.example/invoice.pdf"), 2, CancellationToken.None);

        artifacts.Should().ContainSingle().Which.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        artifacts[0].SourceUrlOrdinal.Should().Be(2);
        transport.Requests.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("chinatax_direct_invoice", "dppt.beijing.chinatax.gov.cn")]
    [InlineData("bwjf_signed_invoice", "fp.bwjf.cn")]
    [InlineData("fpyun_direct_invoice", "sdapi.fpyun.com.cn")]
    [InlineData("pdd_direct_invoice", "files.pdd-fapiao.com")]
    [InlineData("jdcloud_direct_invoice", "eicore-invoice.jdcloud-oss.com")]
    [InlineData("kpbyd_direct_invoice", "etd.kpbyd.com")]
    public async Task Direct_recovery_strategy_retries_and_selects_expected_pdf_for_each_family(string family, string host)
    {
        const string invoiceNumber = "11111111111111111111";
        var sourceUrl = family == "fpyun_direct_invoice"
            ? new Uri($"https://{host}/invoice/qd/download/getInvoiceFile?fptqm=synthetic-capability&invoice={invoiceNumber}")
            : new Uri($"https://{host}/invoice/{invoiceNumber}.pdf?token=synthetic-capability");
        var payload = Encoding.ASCII.GetBytes("%PDF-1.7 synthetic direct invoice");
        var transport = new FakeTransport(
            Response(HttpStatusCode.ServiceUnavailable, [], "text/plain"),
            Response(HttpStatusCode.OK, payload, "application/pdf"));
        var strategy = new DirectInvoiceRecoveryStrategy(NewProbe(transport, maxAttempts: 2));
        var candidate = new MailboxUrlCandidate(
            "acct", "INBOX", "uidvalidity", "77", sourceUrl, family, "group",
            new Dictionary<string, string> { ["invoice_number"] = invoiceNumber }, 0);
        var group = new UrlCandidateGroup(
            family,
            [candidate],
            new Dictionary<string, string> { ["invoice_number"] = invoiceNumber },
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(),
            DocumentIdentity.Create("synthetic-provider-group"));

        var result = await strategy.RecoverAsync(group, CancellationToken.None);

        result.SelectedArtifact.Should().NotBeNull();
        result.SelectedArtifact!.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        result.SelectedArtifact.ExpectedMatch.Should().BeTrue();
        result.SelectedArtifact.MatchReasonCode.Should().Be("invoice_number_from_url");
        result.SelectedArtifact.SanitizedResolvedOrigin.Should().NotContain("synthetic-capability");
        transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Selects_pdf_when_invoice_number_exists_only_in_redirected_url_path()
    {
        const string invoiceNumber = "11111111111111111111";
        var sourceUrl = new Uri("https://files.example/download?token=private-capability");
        var resolvedUrl = $"https://cdn.example/invoices/{invoiceNumber}.pdf?token=redirected-capability";
        var payload = Encoding.ASCII.GetBytes("%PDF-1.7 fixture");
        var transport = new FakeTransport(
            new UrlTransportResponse(HttpStatusCode.Redirect, ReadOnlyMemory<byte>.Empty, "", resolvedUrl),
            Response(HttpStatusCode.OK, payload, "application/pdf"));
        var probe = NewProbe(transport, maxAttempts: 1);
        var strategy = new DirectInvoiceRecoveryStrategy(probe);
        var candidate = new MailboxUrlCandidate("acct", "INBOX", "1", "1", sourceUrl,
            "chinatax_direct_invoice", "group", new Dictionary<string, string>(), 0);
        var group = new UrlCandidateGroup("chinatax_direct_invoice", [candidate],
            new Dictionary<string, string> { ["invoice_number"] = invoiceNumber },
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("group"));

        var result = await strategy.RecoverAsync(group, CancellationToken.None);

        result.SelectedArtifact!.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        result.SelectedArtifact.MatchReasonCode.Should().Be("invoice_number_from_url");
        result.SelectedArtifact.SanitizedResolvedOrigin.Should().Be("https://cdn.example");
        result.SelectedArtifact.SanitizedResolvedOrigin.Should().NotContain("private-capability");
        result.SelectedArtifact.SanitizedResolvedOrigin.Should().NotContain("redirected-capability");
        transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Rejects_zip_member_over_configured_expansion_limit()
    {
        var payload = CreateZip("invoice.pdf", Encoding.ASCII.GetBytes("%PDF-1.7" + new string('x', 128)));
        var transport = new FakeTransport(Response(HttpStatusCode.OK, payload, "application/zip"));
        var probe = NewProbe(transport, maxAttempts: 1, maxArchiveMemberBytes: 32);

        var artifacts = await probe.ProbeAsync(new Uri("https://files.example/invoices.zip"), 0, CancellationToken.None);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Preserves_ofd_container_instead_of_expanding_its_xml_member()
    {
        var payload = CreateZip("OFD.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><OFD />"));
        var transport = new FakeTransport(Response(HttpStatusCode.OK, payload, "application/ofd"));
        var probe = NewProbe(transport, maxAttempts: 1);

        var artifacts = await probe.ProbeAsync(new Uri("https://files.example/invoice.ofd"), 0, CancellationToken.None);

        artifacts.Should().ContainSingle().Which.Kind.Should().Be(RecoveredArtifactKind.Ofd);
        artifacts[0].Content.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task Rejects_zip_with_more_members_than_configured_limit()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7");
        var payload = CreateZip(("one.pdf", pdf), ("two.pdf", pdf));
        var transport = new FakeTransport(Response(HttpStatusCode.OK, payload, "application/zip"));
        var probe = NewProbe(transport, maxAttempts: 1, maxArchiveMembers: 1);

        var artifacts = await probe.ProbeAsync(new Uri("https://files.example/invoices.zip"), 0, CancellationToken.None);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_zip_when_total_expanded_bytes_exceed_configured_limit()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7");
        var payload = CreateZip(("one.pdf", pdf), ("two.pdf", pdf));
        var transport = new FakeTransport(Response(HttpStatusCode.OK, payload, "application/zip"));
        var probe = NewProbe(transport, maxAttempts: 1, maxArchiveTotalBytes: 15);

        var artifacts = await probe.ProbeAsync(new Uri("https://files.example/invoices.zip"), 0, CancellationToken.None);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_retry_public_url_policy_rejection()
    {
        var transport = new FakeTransport(Response(HttpStatusCode.OK, Encoding.ASCII.GetBytes("%PDF-1.7"), "application/pdf"));
        var probe = NewProbe(transport, maxAttempts: 3, resolverAddress: IPAddress.Parse("10.0.0.7"));

        var act = () => probe.ProbeAsync(new Uri("https://files.example/invoice.pdf"), 0, CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "URL_POLICY_REJECTED");
        transport.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Fpyun_legacy_redirect_uses_only_the_approved_literal_ip_and_baiwang_handoff()
    {
        var payload = Encoding.ASCII.GetBytes("%PDF-1.7 fixture");
        var transport = new FakeTransport(
            new UrlTransportResponse(HttpStatusCode.Redirect, ReadOnlyMemory<byte>.Empty, "", "http://203.0.114.7:7100/qd/download/getInvoiceFile?fptqm=fixture"),
            new UrlTransportResponse(HttpStatusCode.Redirect, ReadOnlyMemory<byte>.Empty, "", "http://fp.baiwang.com/format/d?fptqm=fixture"),
            Response(HttpStatusCode.OK, payload, "application/pdf"));
        var probe = NewProbe(transport, maxAttempts: 1);

        var artifacts = await probe.ProbeAsync(
            new Uri("https://sdapi.fpyun.com.cn/invoice/qd/download/getInvoiceFile?fptqm=fixture"),
            0, CancellationToken.None, allowFpyunRedirect: true);

        artifacts.Should().ContainSingle().Which.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        transport.Requests.Select(request => request.Url.Url.Port).Should().Equal(443, 7100, 443);
        transport.Requests[2].Url.Url.Host.Should().Be("fp.baiwang.com");
    }

    [Fact]
    public async Task Fpyun_legacy_redirect_rejects_changed_query()
    {
        var transport = new FakeTransport(new UrlTransportResponse(HttpStatusCode.Redirect, ReadOnlyMemory<byte>.Empty, "",
            "http://203.0.114.7:7100/qd/download/getInvoiceFile?fptqm=other"));
        var probe = NewProbe(transport, maxAttempts: 1);

        var act = () => probe.ProbeAsync(
            new Uri("https://sdapi.fpyun.com.cn/invoice/qd/download/getInvoiceFile?fptqm=fixture"),
            0, CancellationToken.None, allowFpyunRedirect: true);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "URL_POLICY_REJECTED");
        transport.Requests.Should().HaveCount(1);
    }

    private static DirectArtifactProbe NewProbe(
        FakeTransport transport,
        int maxAttempts,
        int maxArchiveMembers = 8,
        long maxArchiveTotalBytes = 4096,
        long maxArchiveMemberBytes = 1024,
        IPAddress? resolverAddress = null)
    {
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>(
            [resolverAddress ?? IPAddress.Parse("203.0.114.7")]));
        var client = new PublicUrlRecoveryClient(policy, transport, 4096, TimeSpan.FromSeconds(2));
        return new DirectArtifactProbe(client, maxAttempts, maxArchiveMembers: maxArchiveMembers,
            maxArchiveTotalBytes: maxArchiveTotalBytes, maxArchiveMemberBytes: maxArchiveMemberBytes,
            delayAsync: static (_, _) => Task.CompletedTask);
    }

    private static byte[] CreateZip(string name, byte[] content)
        => CreateZip((name, content));

    private static byte[] CreateZip(params (string Name, byte[] Content)[] members)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var member in members)
            {
                using var stream = archive.CreateEntry(member.Name).Open();
                stream.Write(member.Content);
            }
        }
        return output.ToArray();
    }

    private static UrlTransportResponse Response(HttpStatusCode status, byte[] content, string contentType)
        => new(status, content, contentType, null);

    private sealed class FakeTransport(params UrlTransportResponse[] responses) : IUrlRecoveryTransport
    {
        private readonly Queue<UrlTransportResponse> _responses = new(responses);
        public List<UrlTransportRequest> Requests { get; } = [];

        public Task<UrlTransportResponse> SendAsync(UrlTransportRequest request, int maxResponseBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}