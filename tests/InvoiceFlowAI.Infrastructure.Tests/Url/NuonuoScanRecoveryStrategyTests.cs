using System.Net;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class NuonuoScanRecoveryStrategyTests
{
    [Fact]
    public async Task Captures_xml_and_pdf_but_selects_one_pdf_primary()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"xmlUrl\":\"https://files.example/invoice.xml\",\"url\":\"https://files.example/invoice.pdf\"}}}";
        var transport = new FakeTransport(
            Response("text/html", []),
            Response("application/json", Encoding.UTF8.GetBytes(detailJson)),
            Response("application/xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />")),
            Response("application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nfixture")));
        var strategy = NewStrategy(transport);

        var result = await strategy.RecoverAsync(Group("preferred_kind", "xml"), CancellationToken.None);

        result.Artifacts.Select(artifact => artifact.Kind)
            .Should().Equal(RecoveredArtifactKind.Xml, RecoveredArtifactKind.Pdf);
        result.SelectedArtifact!.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        transport.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task Selects_single_xml_when_no_pdf_is_available()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"xmlUrl\":\"https://files.example/invoice.xml\"}}}";
        var transport = new FakeTransport(
            Response("text/html", []),
            Response("application/json", Encoding.UTF8.GetBytes(detailJson)),
            Response("application/xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />")));
        var strategy = NewStrategy(transport);

        var result = await strategy.RecoverAsync(Group(), CancellationToken.None);

        result.Artifacts.Should().ContainSingle().Which.Kind.Should().Be(RecoveredArtifactKind.Xml);
        result.SelectedArtifact.Should().BeSameAs(result.Artifacts[0]);
    }

    [Fact]
    public async Task Maps_invalid_detail_json_to_safe_failure()
    {
        var transport = new FakeTransport(
            Response("text/html", []),
            Response("application/json", Encoding.UTF8.GetBytes("{")));

        var act = () => NewStrategy(transport).RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "NUONUO_DETAIL_API_INVALID_RESPONSE");
    }

    [Theory]
    [InlineData("{\"status\":\"1001\"}", "NUONUO_DETAIL_API_UNSUCCESSFUL")]
    [InlineData("{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{}}}", "NUONUO_ARTIFACT_DOWNLOAD_FAILED")]
    public async Task Maps_unsuccessful_or_empty_detail_response_to_safe_failure(string body, string reasonCode)
    {
        var transport = new FakeTransport(
            Response("text/html", []),
            Response("application/json", Encoding.UTF8.GetBytes(body)));

        var act = () => NewStrategy(transport).RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == reasonCode);
    }

    [Fact]
    public async Task Deduplicates_duplicate_artifact_urls_before_download()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"xmlUrl\":\"https://files.example/invoice.pdf\",\"url\":\"https://files.example/invoice.pdf\"}}}";
        var transport = new FakeTransport(
            Response("text/html", []),
            Response("application/json", Encoding.UTF8.GetBytes(detailJson)),
            Response("application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nfixture")));

        var result = await NewStrategy(transport).RecoverAsync(Group(), CancellationToken.None);

        result.Artifacts.Should().ContainSingle().Which.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        transport.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Rejects_multiple_valid_pdfs_as_ambiguous()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"xmlUrl\":\"https://files.example/first.pdf\",\"url\":\"https://files.example/second.pdf\"}}}";
        var transport = new FakeTransport(
            Response("text/html", []),
            Response("application/json", Encoding.UTF8.GetBytes(detailJson)),
            Response("application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nfirst")),
            Response("application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nsecond")));

        var act = () => NewStrategy(transport).RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "NUONUO_ARTIFACT_SELECTION_AMBIGUOUS");
    }

    [Fact]
    public async Task Selects_valid_xml_when_pdf_download_fails()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"xmlUrl\":\"https://files.example/invoice.xml\",\"url\":\"https://files.example/invoice.pdf\"}}}";
        var transport = new FakeTransport(
            Response("text/html", []),
            Response("application/json", Encoding.UTF8.GetBytes(detailJson)),
            Response("application/xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />")),
            new UrlTransportResponse(HttpStatusCode.ServiceUnavailable, ReadOnlyMemory<byte>.Empty, "text/plain", null));

        var result = await NewStrategy(transport).RecoverAsync(Group(), CancellationToken.None);

        result.Artifacts.Should().ContainSingle().Which.Kind.Should().Be(RecoveredArtifactKind.Xml);
        result.SelectedArtifact.Should().BeSameAs(result.Artifacts[0]);
    }

    private static NuonuoScanRecoveryStrategy NewStrategy(FakeTransport transport)
    {
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.114.7")]));
        var client = new PublicUrlRecoveryClient(policy, transport, 1024, TimeSpan.FromSeconds(2));
        return new NuonuoScanRecoveryStrategy(client);
    }

    private static UrlCandidateGroup Group(string? fieldName = null, string? fieldValue = null)
    {
        var fields = fieldName is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [fieldName] = fieldValue! };
        var candidate = new MailboxUrlCandidate(
            "acct", "INBOX", "77", "201",
            new Uri("https://nnfp.jss.com.cn/scan-invoice/printqrcode?paramList=p&code=c&aliView=a&shortLinkSource=s"),
            "nuonuo_scan_invoice", "group", fields, 0);
        return new UrlCandidateGroup("nuonuo_scan_invoice", [candidate], fields,
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("group-id"));
    }

    private static UrlTransportResponse Response(string contentType, byte[] body)
        => new(HttpStatusCode.OK, body, contentType, null);

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