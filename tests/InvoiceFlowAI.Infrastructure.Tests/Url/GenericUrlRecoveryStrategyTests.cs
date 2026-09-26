using System.Net;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class GenericUrlRecoveryStrategyTests
{
    [Fact]
    public async Task Rejects_html_page_instead_of_returning_it_as_pdf()
    {
        var transport = new FakeTransport(Response("text/html", Encoding.UTF8.GetBytes("<html>login</html>")));
        var strategy = NewStrategy(transport);

        var act = () => strategy.RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "GENERIC_URL_NO_VALID_ARTIFACT");
    }

    [Fact]
    public async Task Returns_only_signature_valid_pdf_capture()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 fixture");
        var transport = new FakeTransport(Response("application/pdf", bytes));
        var strategy = NewStrategy(transport);

        var result = await strategy.RecoverAsync(Group(), CancellationToken.None);

        result.Artifacts.Should().ContainSingle().Which.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        result.SelectedArtifact!.Content.ToArray().Should().Equal(bytes);
    }

    private static GenericUrlRecoveryStrategy NewStrategy(FakeTransport transport)
    {
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.114.7")]));
        var client = new PublicUrlRecoveryClient(policy, transport, 4096, TimeSpan.FromSeconds(2));
        return new GenericUrlRecoveryStrategy(new DirectArtifactProbe(client, maxAttempts: 1,
            delayAsync: static (_, _) => Task.CompletedTask));
    }

    private static UrlCandidateGroup Group()
    {
        var candidate = new MailboxUrlCandidate("acct", "INBOX", "1", "1",
            new Uri("https://files.example/invoice.pdf"), "unknown", "group",
            new Dictionary<string, string>(), 0);
        return new UrlCandidateGroup("unknown", [candidate], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("unknown-group"));
    }

    private static UrlTransportResponse Response(string contentType, byte[] content)
        => new(HttpStatusCode.OK, content, contentType, null);

    private sealed class FakeTransport(params UrlTransportResponse[] responses) : IUrlRecoveryTransport
    {
        private readonly Queue<UrlTransportResponse> _responses = new(responses);
        public Task<UrlTransportResponse> SendAsync(UrlTransportRequest request, int maxResponseBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.Dequeue());
        }
    }
}