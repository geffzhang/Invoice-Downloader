using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure;
using InvoiceFlowAI.Infrastructure.Url;
using InvoiceFlowAI.Infrastructure.Url.Worker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class UrlRecoveryPipelineIntegrationTests : IDisposable
{
    private readonly string _jobRoot = Path.Combine(Path.GetTempPath(), "invoiceflow-pipeline-worker-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Production_stage_uses_worker_and_returns_only_selected_primary_artifact()
    {
        var services = new ServiceCollection();
        services.AddInvoiceFlowInfrastructure();
        services.AddSingleton<IUrlRecoveryWorkerProcessRunner>(new FakeRunner());
        services.AddSingleton<ICandidateIdentityFactory, FakeIdentityFactory>();
        var sourceWriter = new FakeSourceWriter();
        services.AddSingleton<ICandidateSourceWriter>(sourceWriter);
        services.AddScoped<UrlRecoveryWorkerClient>(provider => new UrlRecoveryWorkerClient(
            provider.GetRequiredService<IUrlRecoveryWorkerProcessRunner>(),
            provider.GetRequiredService<UrlRecoveryWorkerManifestStore>(),
            _jobRoot));
        services.AddScoped<IUrlRecoveryClient>(provider => provider.GetRequiredService<UrlRecoveryWorkerClient>());
        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var stage = scope.ServiceProvider.GetRequiredService<IUrlRecoveryStage>();
        var group = Group();
        var sourceCandidate = new DocumentCandidate(DocumentIdentity.Create("url-candidate"), 4, "corr", "uid-1",
            "invoice.bin", "application/octet-stream", 0, 0, "url");
        var item = new CandidateWorkItem(sourceCandidate, ReadOnlyMemory<byte>.Empty,
            SourceUrlCandidate: group.Candidates[0], SourceUrlGroup: group);

        var result = await stage.ExecuteAsync(new CandidateBatch([item]), CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].Candidate.DocumentId.Value.Should().Be("recovered-opaque-id");
        result.Items[0].Candidate.OriginalFileName.Should().Be("invoice.pdf");
        result.Items[0].Candidate.ContentType.Should().Be("application/pdf");
        result.Items[0].Content.Span.StartsWith("%PDF-"u8).Should().BeTrue();
        sourceWriter.Candidate.Should().Be(result.Items[0].Candidate);
        sourceWriter.ContentSha256.Should().Be(Convert.ToHexString(SHA256.HashData(result.Items[0].Content.Span)).ToLowerInvariant());
        sourceWriter.SourceGroupIdentity.Value.Should().Be("opaque-group");
        result.EffectiveTerminalResults.Should().BeEmpty();
        Directory.Exists(_jobRoot).Should().BeTrue();
        Directory.GetFileSystemEntries(_jobRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_provider_identity_mismatch_becomes_unresolved_and_blocks_completed_run()
    {
        const string expectedInvoiceNumber = "11111111111111111111";
        const string providerInvoiceNumber = "22222222222222222222";
        var archive = CreateZip(
            ("invoice.xml", Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><Invoice><InvoiceNumber>{providerInvoiceNumber}</InvoiceNumber></Invoice>")),
            ("first.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 first")),
            ("second.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 second")));
        var transport = new FakeDirectTransport(new UrlTransportResponse(HttpStatusCode.OK, archive, "application/zip", null));
        var policy = new PublicUrlPolicy((_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.114.7")]));
        var recoveryClient = new PublicUrlRecoveryClient(policy, transport, 4096, TimeSpan.FromSeconds(2));
        var strategy = new DirectInvoiceRecoveryStrategy(new DirectArtifactProbe(
            recoveryClient, maxAttempts: 1, delayAsync: static (_, _) => Task.CompletedTask));
        var urlCandidate = new MailboxUrlCandidate(
            "acct", "INBOX", "validity", "77", new Uri("https://files.example/invoices.zip"),
            "chinatax_direct_invoice", "mismatch-group",
            new Dictionary<string, string> { ["invoice_number"] = expectedInvoiceNumber }, 0);
        var group = new UrlCandidateGroup(
            urlCandidate.ProviderFamily,
            [urlCandidate],
            new Dictionary<string, string> { ["invoice_number"] = expectedInvoiceNumber },
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(),
            DocumentIdentity.Create("mismatch-group"));
        var candidate = new DocumentCandidate(DocumentIdentity.Create("source-candidate"), 1, "corr", "77",
            "invoice.zip", "application/zip", 0, 0, "url", urlCandidate.SourceUrl);
        var stage = new UrlRecoveryStage(new DirectStrategyClient(strategy));

        var recovered = await stage.ExecuteAsync(new CandidateBatch([
            new CandidateWorkItem(candidate, ReadOnlyMemory<byte>.Empty,
                SourceUrlCandidate: urlCandidate, SourceUrlGroup: group)]), CancellationToken.None);
        var terminal = new TerminalDecisionService().Decide(
            "run-provider-mismatch",
            recovered.EffectiveTerminalResults,
            null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            Array.Empty<RunFailure>(),
            DateTimeOffset.UnixEpoch);

        recovered.Items.Should().BeEmpty();
        recovered.EffectiveTerminalResults.Should().ContainSingle().Which.Status.Should().Be(CandidateStatus.Unresolved);
        recovered.EffectiveTerminalResults[0].Failure!.ReasonCode.Should().Be("DIRECT_INVOICE_PDF_ENTITY_MISMATCH");
        terminal.Status.Should().Be(RunTerminalStatus.PartialSuccess);
        terminal.Summary.UnresolvedCount.Should().Be(1);
        terminal.Status.Should().NotBe(RunTerminalStatus.Completed);
        transport.Requests.Should().ContainSingle();
    }

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

    private static UrlCandidateGroup Group()
    {
        var candidate = new MailboxUrlCandidate("acct", "INBOX", "77", "500",
            new Uri("https://provider.example/download?token=private"), "unknown", "opaque-group",
            new Dictionary<string, string>(), 0);
        return new UrlCandidateGroup("unknown", [candidate], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create("opaque-group"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_jobRoot)) Directory.Delete(_jobRoot, recursive: true);
    }

    private sealed class FakeIdentityFactory : ICandidateIdentityFactory
    {
        public Task<DocumentIdentity> CreateAttachmentAsync(string accountId, string mailbox, string uidValidity,
            MailboxAttachmentCandidate attachment, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DocumentIdentity> CreateUrlAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DocumentIdentity> CreateUrlGroupAsync(string providerFamily, IReadOnlyList<MailboxUrlCandidate> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DocumentIdentity> CreateRecoveredArtifactAsync(DocumentIdentity sourceGroupIdentity, RecoveredArtifactKind kind,
            ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create("recovered-opaque-id"));
    }

    private sealed class FakeSourceWriter : ICandidateSourceWriter
    {
        public DocumentCandidate? Candidate { get; private set; }
        public string? ContentSha256 { get; private set; }
        public DocumentIdentity SourceGroupIdentity { get; private set; }

        public Task UpsertSelectedArtifactAsync(DocumentCandidate candidate, string contentSha256,
            DocumentIdentity sourceGroupIdentity, CancellationToken cancellationToken)
        {
            Candidate = candidate;
            ContentSha256 = contentSha256;
            SourceGroupIdentity = sourceGroupIdentity;
            return Task.CompletedTask;
        }
    }

    private sealed class DirectStrategyClient(DirectInvoiceRecoveryStrategy strategy) : IUrlRecoveryClient
    {
        public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
            => strategy.RecoverAsync(group, cancellationToken);
    }

    private sealed class FakeDirectTransport(UrlTransportResponse response) : IUrlRecoveryTransport
    {
        public List<UrlTransportRequest> Requests { get; } = [];

        public Task<UrlTransportResponse> SendAsync(
            UrlTransportRequest request,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeRunner : IUrlRecoveryWorkerProcessRunner
    {
        public async Task<int> RunAsync(string requestManifestPath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var store = new UrlRecoveryWorkerManifestStore();
            var request = await store.ReadRequestAsync(requestManifestPath, cancellationToken);
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.7 selected artifact");
            var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />");
            await WriteArtifactAsync(request.JobDirectory, "artifacts/capture.pdf", pdf, cancellationToken);
            await WriteArtifactAsync(request.JobDirectory, "artifacts/capture.xml", xml, cancellationToken);
            var response = new UrlRecoveryWorkerResponse(1, null, 0,
            [
                Artifact("artifacts/capture.pdf", RecoveredArtifactKind.Pdf, "application/pdf", pdf),
                Artifact("artifacts/capture.xml", RecoveredArtifactKind.Xml, "application/xml", xml),
            ]);
            await store.WriteResponseAsync(request.ResultManifestPath, response, cancellationToken);
            return 0;
        }

        private static UrlRecoveryWorkerArtifactManifest Artifact(string path, RecoveredArtifactKind kind, string mime, byte[] content)
            => new(path, kind, mime, content.Length, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                0, "https://provider.example", new Dictionary<string, string>(), true, "matched");

        private static async Task WriteArtifactAsync(string root, string relativePath, byte[] content, CancellationToken cancellationToken)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
        }
    }
}