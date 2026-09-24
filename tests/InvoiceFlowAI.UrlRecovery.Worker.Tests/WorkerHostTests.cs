using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using InvoiceFlowAI.UrlRecovery.Worker;
using Xunit;

namespace InvoiceFlowAI.UrlRecovery.Worker.Tests;

public sealed class WorkerHostTests : IDisposable
{
    private readonly string _jobDirectory = Path.Combine(Path.GetTempPath(), "invoiceflow-worker-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Writes_artifact_under_job_directory_and_response_contains_only_relative_path()
    {
        Directory.CreateDirectory(_jobDirectory);
        var requestPath = Path.Combine(_jobDirectory, "request.json");
        var resultPath = Path.Combine(_jobDirectory, "result.json");
        var store = new UrlRecoveryWorkerManifestStore();
        await store.WriteRequestAsync(requestPath,
            new UrlRecoveryWorkerRequest(1, Group(), _jobDirectory, 1024, 8, resultPath), CancellationToken.None);
        var host = new WorkerHost(new FakeClient(), store);

        var exitCode = await host.RunAsync(requestPath, CancellationToken.None);
        var response = await store.ReadResponseAsync(resultPath, CancellationToken.None);

        exitCode.Should().Be(0);
        response.Artifacts.Should().ContainSingle();
        Path.IsPathRooted(response.Artifacts[0].RelativePath).Should().BeFalse();
        File.Exists(Path.Combine(_jobDirectory, response.Artifacts[0].RelativePath)).Should().BeTrue();
        response.SelectedArtifactIndex.Should().Be(0);
    }

    private static UrlCandidateGroup Group()
    {
        var source = new MailboxUrlCandidate("acct", "INBOX", "1", "1", new Uri("https://files.example/a.pdf"),
            "unknown", "group", new Dictionary<string, string>(), 0);
        return new UrlCandidateGroup("unknown", [source], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("opaque-group"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_jobDirectory)) Directory.Delete(_jobDirectory, recursive: true);
    }

    private sealed class FakeClient : IUrlRecoveryClient
    {
        public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
        {
            var content = Encoding.ASCII.GetBytes("%PDF-1.7 worker fixture");
            var artifact = new CapturedUrlArtifact(RecoveredArtifactKind.Pdf, "application/pdf", content, 0,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), "https://files.example",
                new Dictionary<string, string>(), true, "matched_pdf");
            return Task.FromResult(new UrlRecoveryResult([artifact], 0));
        }
    }
}