using System.Text;
using System.Security.Cryptography;
using InvoiceFlowAI.Application.Candidates;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pipeline;

public sealed class UrlRecoveryStageTests
{
    [Fact]
    public async Task Recovers_url_content_passthroughs_attachments_and_preserves_prior_terminal_results()
    {
        var source = UrlSource("https://invoice.example/file?token=fixture");
        var urlItem = WorkItem(source, "url-candidate");
        var attachmentItem = WorkItem(null, "attachment-candidate");
        var priorTerminal = Terminal("already-retained", CandidateStatus.Retained);
        var fetcher = new FakeUrlRecoveryClient(_ => Task.FromResult(new UrlRecoveryResult(new byte[] { 1, 2, 3 }, "application/pdf")));
        var stage = new UrlRecoveryStage(fetcher);

        var output = await stage.ExecuteAsync(new CandidateBatch([urlItem, attachmentItem], [priorTerminal]), CancellationToken.None);

        output.Items.Should().HaveCount(2);
        output.Items[0].Content.ToArray().Should().Equal(1, 2, 3);
        output.Items[0].Candidate.ContentType.Should().Be("application/pdf");
        output.Items[1].Should().BeSameAs(attachmentItem);
        output.EffectiveTerminalResults.Should().ContainSingle().Which.Should().BeSameAs(priorTerminal);
        fetcher.RequestedUris.Should().ContainSingle().Which.Should().Be(source.SourceUrl);
    }

    [Fact]
    public async Task Uses_recovered_artifact_format_instead_of_the_email_preference()
    {
        var source = UrlSource("https://invoice.example/file?token=fixture") with
        {
            ExpectedFields = new Dictionary<string, string> { ["preferred_kind"] = "xml" },
        };
        var item = WorkItem(source, "url-candidate") with
        {
            Candidate = WorkItem(source, "url-candidate").Candidate with { OriginalFileName = "invoice-url.xml" },
        };
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nfixture");
        var stage = new UrlRecoveryStage(new FakeUrlRecoveryClient(
            _ => Task.FromResult(new UrlRecoveryResult(pdf, "application/pdf"))));

        var output = await stage.ExecuteAsync(new CandidateBatch([item]), CancellationToken.None);

        output.Items.Should().ContainSingle();
        output.Items[0].Candidate.OriginalFileName.Should().Be("invoice-url.pdf");
    }

    [Fact]
    public async Task Group_recovery_emits_only_selected_artifact_and_uses_artifact_identity()
    {
        var source = UrlSource("https://invoice.example/file?token=fixture");
        var sourceGroup = Group(source, "group-identity");
        var item = WorkItem(source, "source-candidate") with { SourceUrlGroup = sourceGroup };
        var selectedPdf = Artifact(RecoveredArtifactKind.Pdf, "%PDF-1.7\npdf", "application/pdf");
        var diagnosticXml = Artifact(RecoveredArtifactKind.Xml, "<?xml version=\"1.0\"?><invoice />", "application/xml");
        var result = new UrlRecoveryResult([selectedPdf, diagnosticXml], 0);
        var client = new GroupResultUrlRecoveryClient(result);
        var stage = new UrlRecoveryStage(client, new RecoveredArtifactIdentityFactory());

        var output = await stage.ExecuteAsync(new CandidateBatch([item]), CancellationToken.None);

        output.Items.Should().ContainSingle();
        output.Items[0].Content.ToArray().Should().Equal(selectedPdf.Content.ToArray());
        output.Items[0].Candidate.ContentType.Should().Be("application/pdf");
        output.Items[0].Candidate.OriginalFileName.Should().EndWith(".pdf");
        output.Items[0].Candidate.DocumentId.Value.Should().Be("artifact:Pdf:" + selectedPdf.Sha256);
        client.RequestedGroup.Should().BeSameAs(sourceGroup);
    }

    [Fact]
    public async Task Group_recovery_keeps_python_xml_only_fallback_as_one_primary_item()
    {
        var source = UrlSource("https://invoice.example/file?token=fixture");
        var sourceGroup = Group(source, "group-identity");
        var item = WorkItem(source, "source-candidate") with { SourceUrlGroup = sourceGroup };
        var selectedXml = Artifact(RecoveredArtifactKind.Xml, "<?xml version=\"1.0\"?><invoice />", "application/xml");
        var stage = new UrlRecoveryStage(
            new GroupResultUrlRecoveryClient(new UrlRecoveryResult([selectedXml], 0)),
            new RecoveredArtifactIdentityFactory());

        var output = await stage.ExecuteAsync(new CandidateBatch([item]), CancellationToken.None);

        output.Items.Should().ContainSingle();
        output.Items[0].Content.ToArray().Should().Equal(selectedXml.Content.ToArray());
        output.Items[0].Candidate.ContentType.Should().Be("application/xml");
        output.Items[0].Candidate.OriginalFileName.Should().EndWith(".xml");
    }

    [Fact]
    public async Task Converts_recovery_failure_to_safe_candidate_terminal_result()
    {
        var item = WorkItem(UrlSource("https://invoice.example/file?token=secret"), "failed-candidate");
        var failure = new UrlRecoveryException("URL_RECOVERY_DEADLINE_EXCEEDED", "URL recovery deadline exceeded.", true, true);
        var stage = new UrlRecoveryStage(new FakeUrlRecoveryClient(_ => Task.FromException<UrlRecoveryResult>(failure)));

        var output = await stage.ExecuteAsync(new CandidateBatch([item]), CancellationToken.None);

        output.Items.Should().BeEmpty();
        var terminal = output.EffectiveTerminalResults.Should().ContainSingle().Subject;
        terminal.Status.Should().Be(CandidateStatus.Timeout);
        terminal.Failure!.ReasonCode.Should().Be("URL_RECOVERY_DEADLINE_EXCEEDED");
        terminal.Failure.SafeMessage.Should().NotContain("secret");
    }

    [Fact]
    public async Task Unexpected_provider_exception_does_not_persist_sensitive_diagnostics()
    {
        const string sourceUrl = "https://invoice.example/capability-segment?token=QUERY-SECRET";
        const string rawException = "LOG-EXCEPTION-SECRET BUYER-SECRET SENDER-SECRET MAILBOX-SUBJECT-SECRET";
        var item = WorkItem(UrlSource(sourceUrl), "privacy-candidate");
        var stage = new UrlRecoveryStage(new FakeUrlRecoveryClient(
            _ => Task.FromException<UrlRecoveryResult>(new InvalidOperationException(rawException))));

        var output = await stage.ExecuteAsync(new CandidateBatch([item]), CancellationToken.None);

        var failure = output.EffectiveTerminalResults.Should().ContainSingle().Which.Failure!;
        var persistedDiagnostic = string.Join('|', failure.ReasonCode, failure.SafeMessage, failure.ExceptionType, failure.Fingerprint);
        persistedDiagnostic.Should().Be("URL_RECOVERY_WORKER_FAILED|Invoice link could not be recovered.||");
        persistedDiagnostic.Should().NotContain("capability-segment").And.NotContain("QUERY-SECRET")
            .And.NotContain("LOG-EXCEPTION-SECRET").And.NotContain("BUYER-SECRET")
            .And.NotContain("SENDER-SECRET").And.NotContain("MAILBOX-SUBJECT-SECRET");
    }

    [Fact]
    public async Task Propagates_cancellation_without_creating_terminal_failure()
    {
        var item = WorkItem(UrlSource("https://invoice.example/file"), "canceled-candidate");
        var stage = new UrlRecoveryStage(new FakeUrlRecoveryClient(_ => Task.FromException<UrlRecoveryResult>(new OperationCanceledException())));

        var act = () => stage.ExecuteAsync(new CandidateBatch([item]), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Caller_cancellation_terminates_a_blocked_recovery_request()
    {
        var item = WorkItem(UrlSource("https://invoice.example/file"), "blocked-candidate");
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BlockingUrlRecoveryClient(requestStarted, cancellationObserved);
        var stage = new UrlRecoveryStage(client);
        using var cancellation = new CancellationTokenSource();

        var execution = stage.ExecuteAsync(new CandidateBatch([item]), cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Recovery_runs_at_most_ten_independent_groups_and_preserves_result_order()
    {
        var client = new BarrierGroupRecoveryClient(10);
        var stage = new UrlRecoveryStage(client, new RecoveredArtifactIdentityFactory());
        var items = Enumerable.Range(0, 12)
            .Select(index => GroupWorkItem(index, "generic"))
            .ToArray();
        var execution = stage.ExecuteAsync(new CandidateBatch(items), CancellationToken.None);

        await client.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.MaximumActive.Should().Be(10);
        client.Release.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        result.Items.Select(item => item.Candidate.Sequence).Should().Equal(Enumerable.Range(0, 12).Select(index => (long)index));
        client.MaximumActive.Should().Be(10);
    }

    [Fact]
    public async Task Strong_provider_recovery_runs_at_most_four_groups_at_once()
    {
        var client = new BarrierGroupRecoveryClient(4);
        var stage = new UrlRecoveryStage(client, new RecoveredArtifactIdentityFactory());
        var items = Enumerable.Range(0, 9)
            .Select(index => GroupWorkItem(index, "chinatax_direct_invoice"))
            .ToArray();
        var execution = stage.ExecuteAsync(new CandidateBatch(items), CancellationToken.None);

        await client.ThresholdReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.MaximumActive.Should().Be(4);
        client.Release.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        result.Items.Should().HaveCount(9);
        client.MaximumActive.Should().Be(4);
    }

    [Fact]
    public async Task Candidates_inside_one_provider_group_are_recovered_serially()
    {
        var candidates = Enumerable.Range(0, 3)
            .Select(index => UrlSource($"https://provider.example/{index}") with
            {
                ProviderFamily = "chinatax_direct_invoice",
                Sequence = index,
            })
            .ToArray();
        var client = new SerialTrackingUrlRecoveryClient();

        await ((IUrlRecoveryClient)client).RecoverAsync(Group(candidates, "same-provider-group"), CancellationToken.None);

        client.RequestedUris.Should().Equal(candidates.Select(candidate => candidate.SourceUrl));
        client.MaximumActive.Should().Be(1);
    }

    private static CandidateWorkItem WorkItem(MailboxUrlCandidate? source, string identity)
    {
        var candidate = new DocumentCandidate(
            DocumentIdentity.Create(identity),
            1,
            identity,
            "message-1",
            source is null ? "invoice.pdf" : "invoice-url.pdf",
            source is null ? "application/pdf" : "application/octet-stream",
            0,
            0,
            source is null ? "mime_attachment" : "url",
            source?.SourceUrl);
        return new CandidateWorkItem(candidate, ReadOnlyMemory<byte>.Empty, SourceUrlCandidate: source);
    }

    private static MailboxUrlCandidate UrlSource(string uri)
        => new("account-1", "INBOX", "77", "201", new Uri(uri), "", "", new Dictionary<string, string>(), 0);

    private static UrlCandidateGroup Group(MailboxUrlCandidate candidate, string identity)
        => new(candidate.ProviderFamily, [candidate], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create(identity));

    private static UrlCandidateGroup Group(IReadOnlyList<MailboxUrlCandidate> candidates, string identity)
        => new(candidates[0].ProviderFamily, candidates, new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(), DocumentIdentity.Create(identity));

    private static CandidateWorkItem GroupWorkItem(int index, string providerFamily)
    {
        var source = UrlSource($"https://recovery.example/{index}") with
        {
            ProviderFamily = providerFamily,
            ProviderGroupId = $"group-{index}",
            Sequence = index,
        };
        return WorkItem(source, $"source-{index}") with
        {
            SourceUrlGroup = Group(source, $"group-identity-{index}"),
            Candidate = WorkItem(source, $"source-{index}").Candidate with { Sequence = index },
        };
    }

    private static CapturedUrlArtifact Artifact(RecoveredArtifactKind kind, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new CapturedUrlArtifact(kind, contentType, bytes, 0,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "https://files.fixture.invalid",
            new Dictionary<string, string>(), null, "NO_EXPECTED_FIELDS");
    }

    private sealed class GroupResultUrlRecoveryClient(UrlRecoveryResult result) : IUrlRecoveryClient
    {
        public UrlCandidateGroup? RequestedGroup { get; private set; }

        public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
        {
            RequestedGroup = group;
            return Task.FromResult(result);
        }
    }

    private sealed class RecoveredArtifactIdentityFactory : ICandidateIdentityFactory
    {
        public Task<DocumentIdentity> CreateAttachmentAsync(
            string accountId, string mailbox, string uidValidity, MailboxAttachmentCandidate attachment,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DocumentIdentity> CreateUrlAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DocumentIdentity> CreateUrlGroupAsync(
            string providerFamily, IReadOnlyList<MailboxUrlCandidate> candidates, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DocumentIdentity> CreateRecoveredArtifactAsync(
            DocumentIdentity sourceGroupIdentity, RecoveredArtifactKind kind, ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            var digest = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
            return Task.FromResult(DocumentIdentity.Create($"artifact:{kind}:{digest}"));
        }
    }

    private static CandidateProcessResult Terminal(string identity, CandidateStatus status)
        => new(new DocumentCandidate(DocumentIdentity.Create(identity), 0, identity, "message-1", "retained.pdf", "application/pdf", 1, 0, "mime_attachment"), status);

    private sealed class FakeUrlRecoveryClient(Func<Uri, Task<UrlRecoveryResult>> recover)
        : IUrlRecoveryClient
    {
        public List<Uri> RequestedUris { get; } = [];

        public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
        {
            RequestedUris.Add(sourceUrl);
            cancellationToken.ThrowIfCancellationRequested();
            return recover(sourceUrl);
        }
    }

    private sealed class BlockingUrlRecoveryClient(
        TaskCompletionSource requestStarted,
        TaskCompletionSource cancellationObserved) : IUrlRecoveryClient
    {
        public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
        {
            requestStarted.TrySetResult();
            return WaitForCancellationAsync(cancellationToken);
        }

        private async Task<UrlRecoveryResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocked request unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class BarrierGroupRecoveryClient(int threshold) : IUrlRecoveryClient
    {
        private readonly object _sync = new();
        private int _active;
        public TaskCompletionSource ThresholdReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaximumActive { get; private set; }

        public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public async Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _active++;
                MaximumActive = Math.Max(MaximumActive, _active);
                if (_active >= threshold) ThresholdReached.TrySetResult();
            }

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 barrier result");
                return new UrlRecoveryResult(bytes, "application/pdf");
            }
            finally
            {
                lock (_sync) _active--;
            }
        }
    }

    private sealed class SerialTrackingUrlRecoveryClient : IUrlRecoveryClient
    {
        private int _active;
        public List<Uri> RequestedUris { get; } = [];
        public int MaximumActive { get; private set; }

        public async Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _active++;
            MaximumActive = Math.Max(MaximumActive, _active);
            RequestedUris.Add(sourceUrl);
            try
            {
                await Task.Yield();
                return new UrlRecoveryResult(Encoding.ASCII.GetBytes("%PDF-1.7 serial"), "application/pdf");
            }
            finally
            {
                _active--;
            }
        }
    }
}
