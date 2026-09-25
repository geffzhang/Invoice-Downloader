using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class UrlRecoveryWorkerClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "invoiceflow-worker-client-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Reads_verified_artifact_then_cleans_job_directory()
    {
        var runner = new FakeRunner(artifactBytes: Encoding.ASCII.GetBytes("%PDF-1.7 worker"));
        var client = NewClient(runner);

        var result = await client.RecoverAsync(Group(), CancellationToken.None);

        result.SelectedArtifact!.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        result.SelectedArtifact.Content.ToArray().Should().Equal(runner.ArtifactBytes);
        Directory.Exists(runner.JobDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Cleanup_failure_overrides_success_with_safe_failure()
    {
        var runner = new FakeRunner(artifactBytes: Encoding.ASCII.GetBytes("%PDF-1.7 worker"));
        var client = new UrlRecoveryWorkerClient(
            runner,
            new UrlRecoveryWorkerManifestStore(),
            _root,
            TimeSpan.FromSeconds(2),
            _ => false);
        var group = Group("https://files.example/invoice.pdf?token=source-secret&cookie=cookie-secret&header=header-secret");

        var act = () => client.RecoverAsync(group, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UrlRecoveryException>();
        exception.Which.ReasonCode.Should().Be("URL_RECOVERY_WORKER_CLEANUP_FAILED");
        exception.Which.ToString().Should().NotContain("files.example");
        exception.Which.ToString().Should().NotContain("source-secret");
        exception.Which.ToString().Should().NotContain("cookie-secret");
        exception.Which.ToString().Should().NotContain("header-secret");
        var opaqueJobId = Path.GetFileName(runner.JobDirectory);
        opaqueJobId.Should().MatchRegex("^[a-f0-9]{32}$");
        exception.Which.Message.Should().Contain(opaqueJobId).And.Contain("cleanup is incomplete");
        exception.Which.Message.Should().NotContain(_root);
        exception.Which.Message.Should().NotContain(runner.JobDirectory);
        Directory.Exists(runner.JobDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_digest_mismatch_and_cleans_job_directory()
    {
        var runner = new FakeRunner(artifactBytes: Encoding.ASCII.GetBytes("%PDF-1.7 worker"), corruptDigest: true);
        var client = NewClient(runner);

        var act = () => client.RecoverAsync(Group(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UrlRecoveryException>();
        runner.SetupFailure.Should().BeNull();
        exception.Which.ReasonCode.Should().Be("URL_RECOVERY_WORKER_OUTPUT_INVALID");
        Directory.Exists(runner.JobDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_traversal_manifest_and_cleans_job_directory()
    {
        var runner = new FakeRunner(artifactBytes: Encoding.ASCII.GetBytes("%PDF-1.7 worker"), relativePath: "../outside.pdf");
        var client = NewClient(runner);

        var act = () => client.RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "URL_RECOVERY_WORKER_OUTPUT_INVALID");
        Directory.Exists(runner.JobDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Maps_worker_timeout_to_safe_failure_and_cleans_job_directory()
    {
        var runner = new FakeRunner(timeout: true);
        var client = NewClient(runner);

        var act = () => client.RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "URL_RECOVERY_WORKER_TIMEOUT");
        Directory.Exists(runner.JobDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_artifact_with_source_ordinal_outside_requested_group()
    {
        var runner = new FakeRunner(artifactBytes: Encoding.ASCII.GetBytes("%PDF-1.7 worker"), sourceOrdinal: 1);
        var client = NewClient(runner);

        var act = () => client.RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "URL_RECOVERY_WORKER_OUTPUT_INVALID");
        Directory.Exists(runner.JobDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_missing_artifact_file_as_safe_output_failure()
    {
        var runner = new FakeRunner(artifactBytes: Encoding.ASCII.GetBytes("%PDF-1.7 worker"), omitArtifact: true);
        var client = NewClient(runner);

        var act = () => client.RecoverAsync(Group(), CancellationToken.None);

        await act.Should().ThrowAsync<UrlRecoveryException>()
            .Where(exception => exception.ReasonCode == "URL_RECOVERY_WORKER_OUTPUT_INVALID");
        Directory.Exists(runner.JobDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_artifact_path_through_junction_outside_job_directory()
    {
        var runner = new FakeRunner(artifactBytes: Encoding.ASCII.GetBytes("%PDF-1.7 worker"), linkDirectoryOutsideJob: true);
        var client = NewClient(runner);

        var act = () => client.RecoverAsync(Group(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<UrlRecoveryException>();
        runner.SetupFailure.Should().BeNull();
        exception.Which.ReasonCode.Should().Be("URL_RECOVERY_WORKER_OUTPUT_INVALID");
        Directory.Exists(runner.JobDirectory).Should().BeFalse();
        File.Exists(runner.OutsideArtifactPath).Should().BeTrue();
    }

    private UrlRecoveryWorkerClient NewClient(FakeRunner runner)
        => new(runner, new UrlRecoveryWorkerManifestStore(), _root, TimeSpan.FromSeconds(2));

    private static UrlCandidateGroup Group(string sourceUrl = "https://files.example/invoice.pdf")
    {
        var candidate = new MailboxUrlCandidate("acct", "INBOX", "1", "1", new Uri(sourceUrl),
            "unknown", "opaque-group", new Dictionary<string, string>(), 0);
        return new UrlCandidateGroup("unknown", [candidate], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("opaque-group"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeRunner(
        byte[]? artifactBytes = null,
        bool corruptDigest = false,
        string relativePath = "artifacts/out.pdf",
        bool timeout = false,
        int sourceOrdinal = 0,
        bool omitArtifact = false,
        bool linkDirectoryOutsideJob = false) : IUrlRecoveryWorkerProcessRunner
    {
        public string? JobDirectory { get; private set; }
        public string? SetupFailure { get; private set; }
        public string? OutsideArtifactPath { get; private set; }
        public byte[] ArtifactBytes { get; } = artifactBytes ?? Encoding.ASCII.GetBytes("%PDF-1.7 worker");

        public async Task<int> RunAsync(string requestManifestPath, TimeSpan timeoutValue, CancellationToken cancellationToken)
        {
            if (timeout) throw new UrlRecoveryWorkerTimeoutException();
            var store = new UrlRecoveryWorkerManifestStore();
            var request = await store.ReadRequestAsync(requestManifestPath, cancellationToken);
            JobDirectory = request.JobDirectory;
            var absoluteArtifactPath = Path.GetFullPath(Path.Combine(request.JobDirectory, relativePath));
            if (!omitArtifact && !relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                if (linkDirectoryOutsideJob)
                {
                    var outsideDirectory = Path.Combine(Path.GetDirectoryName(request.JobDirectory)!, "outside-artifacts");
                    Directory.CreateDirectory(outsideDirectory);
                    OutsideArtifactPath = Path.Combine(outsideDirectory, Path.GetFileName(absoluteArtifactPath));
                    await File.WriteAllBytesAsync(OutsideArtifactPath, ArtifactBytes, cancellationToken);
                    var junctionPath = Path.Combine(request.JobDirectory, "artifacts");
                    var startInfo = new ProcessStartInfo("cmd.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        Arguments = $"/c mklink /J \"{junctionPath}\" \"{outsideDirectory}\"",
                    };
                    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not create test junction.");
                    await process.WaitForExitAsync(cancellationToken);
                    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                    if (process.ExitCode != 0)
                    {
                        SetupFailure = $"junction creation exit code {process.ExitCode}: {output} {error}";
                        throw new InvalidOperationException("Could not create test junction.");
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(absoluteArtifactPath)!);
                }
                await File.WriteAllBytesAsync(absoluteArtifactPath, ArtifactBytes, cancellationToken);
            }

            var digest = Convert.ToHexString(SHA256.HashData(ArtifactBytes)).ToLowerInvariant();
            if (corruptDigest) digest = new string('0', 64);
            if (relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                var maliciousJson = $$"""{"schemaVersion":1,"failureReasonCode":null,"selectedArtifactIndex":0,"artifacts":[{"relativePath":{{JsonSerializer.Serialize(relativePath)}},"kind":0,"contentType":"application/pdf","byteLength":{{ArtifactBytes.Length}},"sha256":"{{digest}}","sourceUrlOrdinal":0,"sanitizedResolvedOrigin":"https://files.example","invoiceFields":{},"expectedMatch":true,"matchReasonCode":"matched_pdf"}]}""";
                await File.WriteAllTextAsync(request.ResultManifestPath, maliciousJson, cancellationToken);
            }
            else
            {
                var response = new UrlRecoveryWorkerResponse(1, null, 0,
                [
                    new UrlRecoveryWorkerArtifactManifest(relativePath, RecoveredArtifactKind.Pdf,
                        "application/pdf", ArtifactBytes.Length, digest, sourceOrdinal, "https://files.example",
                        new Dictionary<string, string>(), true, "matched_pdf"),
                ]);
                await store.WriteResponseAsync(request.ResultManifestPath, response, cancellationToken);
            }
            return 0;
        }
    }
}