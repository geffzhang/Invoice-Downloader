using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class BaiwangRecoveryStrategyTests
{
    [Fact]
    public async Task Posts_param_to_fixed_preview_api_and_selects_pdf_related_to_matching_xml()
    {
        var transport = new FakeTransport(
            Response(HttpStatusCode.NotFound, [], "text/plain"),
            Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"success\":true}"), "application/json"),
            Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Invoice><InvoiceNumber>12345678</InvoiceNumber></Invoice>"), "application/xml"),
            Response(HttpStatusCode.OK, Encoding.ASCII.GetBytes("%PDF-1.7 fixture"), "application/pdf"),
            Response(HttpStatusCode.OK, [0x50, 0x4B, 0x03, 0x04, 0x6F, 0x66, 0x64], "application/vnd.ofd"));
        var strategy = NewStrategy(transport);

        var result = await strategy.RecoverAsync(Group(), CancellationToken.None);

        result.Artifacts.Select(artifact => artifact.Kind)
            .Should().Equal(RecoveredArtifactKind.Xml, RecoveredArtifactKind.Pdf, RecoveredArtifactKind.Ofd);
        result.SelectedArtifact!.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        result.SelectedArtifact.ExpectedMatch.Should().BeTrue();
        result.SelectedArtifact.MatchReasonCode.Should().Be("xml_then_pdf_same_source");
        transport.Requests[1].Method.Should().Be(HttpMethod.Post);
        transport.Requests[1].Body!.Value.ToArray().Should().Equal(Encoding.UTF8.GetBytes("secret-param"));
        transport.Requests[1].ContentType.Should().Be("application/json; charset=utf-8");
        transport.Requests[1].Url.Url.Host.Should().Be("pis.baiwang.com");
        transport.Requests[2].Url.Url.Query.Should().Contain("formatType=XML");
        transport.Requests.Select(request => request.Url.Url.Query)
            .Should().Contain(query => query.Contains("formatType=OFD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejects_wrapper_pdf_using_extracted_pdf_text()
    {
        var transport = new FakeTransport(
            Response(HttpStatusCode.OK, CreateTextPdf("previewinvoice downloadpdf"), "application/pdf"),
            Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"success\":false}"), "application/json"));
        var strategy = NewStrategy(transport);

        var act = () => strategy.RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "BAIWANG_NO_VALID_PDF");
    }

    private static BaiwangRecoveryStrategy NewStrategy(FakeTransport transport)
    {
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.114.7")]));
        var client = new PublicUrlRecoveryClient(policy, transport, 4096, TimeSpan.FromSeconds(2));
        var probe = new DirectArtifactProbe(client, maxAttempts: 1,
            delayAsync: static (_, _) => Task.CompletedTask);
        return new BaiwangRecoveryStrategy(client, probe);
    }

    private static UrlCandidateGroup Group()
    {
        var candidate = new MailboxUrlCandidate("acct", "INBOX", "77", "301",
            new Uri("https://pis.baiwang.com/mailLink?param=secret-param"), "baiwang", "group",
            new Dictionary<string, string>(), 0);
        return new UrlCandidateGroup("baiwang", [candidate],
            new Dictionary<string, string> { ["invoice_number"] = "12345678" },
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("baiwang-group"));
    }

    private static UrlTransportResponse Response(HttpStatusCode status, byte[] content, string contentType)
        => new(status, content, contentType, null);

    private static byte[] CreateTextPdf(string text)
    {
        var streamContent = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(streamContent)} >>\nstream\n{streamContent}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

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