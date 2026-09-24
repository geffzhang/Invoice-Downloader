using System.Net;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class PublicUrlRecoveryClientTests
{
    [Fact]
    public async Task Revalidates_each_redirect_and_returns_bounded_response_content()
    {
        var start = new Uri("https://first.example/invoice?token=first");
        var next = new Uri("https://second.example/download?token=second");
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.Redirect, ReadOnlyMemory<byte>.Empty, "", next.ToString()),
            new UrlTransportResponse(HttpStatusCode.OK, new byte[] { 1, 2, 3 }, "application/pdf", null));
        var client = NewClient(transport);

        var result = await client.RecoverAsync(start, CancellationToken.None);

        result.Content.ToArray().Should().Equal(1, 2, 3);
        result.ContentType.Should().Be("application/pdf");
        transport.Requests.Select(request => request.Url.Host).Should().Equal("first.example", "second.example");
    }

    [Fact]
    public async Task Rejects_redirect_to_localhost_before_sending_another_request()
    {
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.Redirect, ReadOnlyMemory<byte>.Empty, "", "http://localhost/private"));
        var client = NewClient(transport);

        var act = () => client.RecoverAsync(new Uri("https://first.example/invoice"), CancellationToken.None);

        var error = await act.Should().ThrowAsync<UrlRecoveryException>();
        error.Which.ReasonCode.Should().Be("URL_POLICY_REJECTED");
        transport.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Rejects_response_body_over_configured_limit()
    {
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.OK, new byte[] { 1, 2, 3, 4 }, "application/pdf", null));
        var client = NewClient(transport, maxBytes: 3);

        var act = () => client.RecoverAsync(new Uri("https://first.example/invoice"), CancellationToken.None);

        var error = await act.Should().ThrowAsync<UrlRecoveryException>();
        error.Which.ReasonCode.Should().Be("URL_RECOVERY_RESPONSE_TOO_LARGE");
        error.Which.Message.Should().NotContain("first.example");
    }

    [Fact]
    public async Task Nuonuo_shortlink_posts_detail_parameters_then_downloads_returned_artifact()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"xmlUrl\":\"https://files.example/invoice.xml\",\"url\":\"https://files.example/invoice.pdf\"}}}";
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.OK, ReadOnlyMemory<byte>.Empty, "text/html", null),
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(detailJson), "application/json", null),
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<invoice />"), "application/xml", null),
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.ASCII.GetBytes("%PDF-1.7\nfixture"), "application/pdf", null));
        var client = NewClient(transport);
        var candidate = new MailboxUrlCandidate(
            "acct-1", "INBOX", "77", "203",
            new Uri("https://nnfp.jss.com.cn/scan-invoice/printqrcode?paramList=p&code=c&aliView=a&shortLinkSource=s"),
            "nuonuo_scan_invoice", "group", new Dictionary<string, string> { ["preferred_kind"] = "xml" }, 0);

        var result = await client.RecoverAsync(candidate, CancellationToken.None);

        result.Content.ToArray().Should().StartWith(Encoding.ASCII.GetBytes("%PDF-1.7"));
        result.ContentType.Should().Be("application/pdf");
        transport.Requests.Select(request => request.Url.Host).Should().Equal(
            "nnfp.jss.com.cn", "nnfp.jss.com.cn", "files.example", "files.example");
        transport.Requests[1].Method.Should().Be(HttpMethod.Post);
        transport.Requests[1].FormFields.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["paramList"] = "p",
            ["code"] = "c",
            ["aliView"] = "a",
            ["invoiceDetailMiddleUri"] = "printQrcode",
            ["shortLinkSource"] = "s",
        });
        transport.Requests[0].Method.Should().Be(HttpMethod.Get);
        transport.Requests[2].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task Nuonuo_xml_only_response_is_not_returned_as_a_recovered_pdf()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"xmlUrl\":\"https://files.example/invoice.xml\"}}}";
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.OK, ReadOnlyMemory<byte>.Empty, "text/html", null),
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(detailJson), "application/json", null),
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />"), "application/xml", null));
        var client = NewClient(transport);
        var candidate = new MailboxUrlCandidate(
            "acct-1", "INBOX", "77", "208",
            new Uri("https://nnfp.jss.com.cn/scan-invoice/printqrcode?paramList=p"),
            "nuonuo_scan_invoice", "group", new Dictionary<string, string>(), 0);

        var act = () => client.RecoverAsync(candidate, CancellationToken.None);

        var error = await act.Should().ThrowAsync<UrlRecoveryException>();
        error.Which.ReasonCode.Should().Be("NUONUO_ARTIFACT_DOWNLOAD_FAILED");
    }

    [Fact]
    public async Task Nuonuo_html_error_page_is_not_returned_as_a_document()
    {
        const string detailJson = "{\"status\":\"0000\",\"data\":{\"invoiceSimpleVo\":{\"url\":\"https://files.example/invoice.pdf\"}}}";
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.OK, ReadOnlyMemory<byte>.Empty, "text/html", null),
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(detailJson), "application/json", null),
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>session expired</html>"), "text/html", null));
        var client = NewClient(transport);
        var candidate = new MailboxUrlCandidate(
            "acct-1", "INBOX", "77", "204",
            new Uri("https://nnfp.jss.com.cn/scan-invoice/printqrcode?paramList=p"),
            "nuonuo_scan_invoice", "group", new Dictionary<string, string>(), 0);

        var act = () => client.RecoverAsync(candidate, CancellationToken.None);

        var error = await act.Should().ThrowAsync<UrlRecoveryException>();
        error.Which.ReasonCode.Should().Be("NUONUO_ARTIFACT_DOWNLOAD_FAILED");
    }

    [Fact]
    public async Task Nuonuo_detail_post_does_not_forward_capability_fields_to_cross_origin_redirect()
    {
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.OK, ReadOnlyMemory<byte>.Empty, "text/html", null),
            new UrlTransportResponse(HttpStatusCode.RedirectMethod, ReadOnlyMemory<byte>.Empty, "", "https://attacker.example/collect"));
        var client = NewClient(transport);
        var candidate = new MailboxUrlCandidate(
            "acct-1", "INBOX", "77", "205",
            new Uri("https://nnfp.jss.com.cn/scan-invoice/printqrcode?paramList=capability-secret"),
            "nuonuo_scan_invoice", "group", new Dictionary<string, string>(), 0);

        var act = () => client.RecoverAsync(candidate, CancellationToken.None);

        var error = await act.Should().ThrowAsync<UrlRecoveryException>();
        error.Which.ReasonCode.Should().Be("URL_RECOVERY_CROSS_ORIGIN_POST_REDIRECT_BLOCKED");
        transport.Requests.Should().HaveCount(2);
        transport.Requests[1].FormFields.Should().ContainKey("paramList");
    }

    [Theory]
    [InlineData("chinatax_direct_invoice", "https://dppt.beijing.chinatax.gov.cn/kpfw/fpjfzz/v1/exportdzfpwjewm")]
    [InlineData("bwjf_signed_invoice", "https://fp.bwjf.cn/downsigninvoice?code=secret")]
    [InlineData("fpyun_direct_invoice", "https://sdapi.fpyun.com.cn/invoice/qd/download/getinvoicefile?fptqm=fixture")]
    [InlineData("pdd_direct_invoice", "https://files.pdd-fapiao.com/invoice/pdf/a.pdf")]
    [InlineData("jdcloud_direct_invoice", "https://eicore-invoice-25.s3.cn-north-1.jdcloud-oss.com/digital-invoice/a.pdf?Signature=secret")]
    [InlineData("kpbyd_direct_invoice", "https://etd.kpbyd.com/hub/files/download?fileCode=abc_pdf")]
    public async Task Direct_provider_candidate_accepts_a_valid_pdf_artifact(string family, string rawUrl)
    {
        var payload = Encoding.ASCII.GetBytes("%PDF-1.7\nprovider fixture");
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.OK, payload, "application/pdf", null));
        var client = NewClient(transport);
        var candidate = new MailboxUrlCandidate(
            "acct-1", "INBOX", "77", "206", new Uri(rawUrl), family, "group",
            new Dictionary<string, string> { ["preferred_kind"] = "pdf" }, 0);

        var result = await client.RecoverAsync(candidate, CancellationToken.None);

        result.Content.ToArray().Should().Equal(payload);
    }

    [Theory]
    [InlineData("chinatax_direct_invoice", "https://dppt.beijing.chinatax.gov.cn/kpfw/fpjfzz/v1/exportdzfpwjewm")]
    [InlineData("bwjf_signed_invoice", "https://fp.bwjf.cn/downsigninvoice?code=secret")]
    [InlineData("fpyun_direct_invoice", "https://sdapi.fpyun.com.cn/invoice/qd/download/getinvoicefile?fptqm=fixture")]
    [InlineData("pdd_direct_invoice", "https://files.pdd-fapiao.com/invoice/pdf/a.pdf")]
    [InlineData("jdcloud_direct_invoice", "https://eicore-invoice-25.s3.cn-north-1.jdcloud-oss.com/digital-invoice/a.pdf")]
    [InlineData("kpbyd_direct_invoice", "https://etd.kpbyd.com/hub/files/download?fileCode=abc_pdf")]
    public async Task Direct_provider_candidate_rejects_non_pdf_response(string family, string rawUrl)
    {
        var transport = new FakeUrlRecoveryTransport(
            new UrlTransportResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>login required</html>"), "text/html", null));
        var client = NewClient(transport);
        var candidate = new MailboxUrlCandidate(
            "acct-1", "INBOX", "77", "207", new Uri(rawUrl), family, "group",
            new Dictionary<string, string> { ["preferred_kind"] = "pdf" }, 0);

        var act = () => client.RecoverAsync(candidate, CancellationToken.None);

        var error = await act.Should().ThrowAsync<UrlRecoveryException>();
        error.Which.ReasonCode.Should().Be("DIRECT_INVOICE_NO_VALID_PDF_RECOVERED");
    }

    private static PublicUrlRecoveryClient NewClient(FakeUrlRecoveryTransport transport, int maxBytes = 1024)
    {
        var policy = new PublicUrlPolicy(
            (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.114.7")]));
        return new PublicUrlRecoveryClient(policy, transport, maxBytes, TimeSpan.FromSeconds(2));
    }

    private sealed class FakeUrlRecoveryTransport(params UrlTransportResponse[] responses) : IUrlRecoveryTransport
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
