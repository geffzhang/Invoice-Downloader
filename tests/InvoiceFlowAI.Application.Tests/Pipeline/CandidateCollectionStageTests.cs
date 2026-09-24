using FluentAssertions;
using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pipeline;

public sealed class CandidateCollectionStageTests
{
    [Fact]
    public async Task Collects_main_chain_attachments_and_url_candidates_in_message_order()
    {
        var identities = new FakeIdentityFactory();
        var stage = new CandidateCollectionStage(identities, new FakeHistoryReader());
        var first = Attachment("message-1", "first.pdf", [1, 2, 3], "main_chain");
        var second = Attachment("message-2", "second.pdf", [4, 5, 6], "main_chain");
        var url = Url("message-1", 1, "https://fixture.invalid/invoice?sig=fake");
        var scan = Scan(
            new[] { Message("message-1"), Message("message-2") },
            new[] { first, second },
            new[] { url });

        var batch = await stage.ExecuteAsync(scan, CancellationToken.None);

        batch.Items.Select(item => item.Candidate.OriginalFileName)
            .Should()
            .Equal("first.pdf", "invoice-url.pdf", "second.pdf");
        batch.Items.Select(item => item.Candidate.Sequence).Should().Equal(0, 1, 2);
        batch.Items[0].Content.ToArray().Should().Equal(new byte[] { 1, 2, 3 });
        batch.Items[1].Content.IsEmpty.Should().BeTrue();
        batch.Items[1].SourceUrlCandidate.Should().BeSameAs(url);
        batch.Items[1].Candidate.SourceUrl.Should().Be(url.SourceUrl);
        batch.Items[1].Candidate.Metadata.Should().NotContainKey("invoice_number");
        batch.EffectiveTerminalResults.Should().BeEmpty();
    }

    [Fact]
    public async Task Groups_urls_by_message_and_provider_preserving_discovery_order()
    {
        var stage = new CandidateCollectionStage(new FakeIdentityFactory(), new FakeHistoryReader());
        var first = Url("message-1", 0, "https://fixture.invalid/a?sig=a");
        var second = Url("message-1", 1, "https://fixture.invalid/b?sig=b");
        var otherMessage = Url("message-2", 2, "https://fixture.invalid/c?sig=c");
        var unknown = Url("message-1", 3, "https://unknown.invalid/d?sig=d") with { ProviderFamily = string.Empty };

        var batch = await stage.ExecuteAsync(
            Scan(
                new[] { Message("message-1"), Message("message-2") },
                Array.Empty<MailboxAttachmentCandidate>(),
                new[] { first, second, otherMessage, unknown }),
            CancellationToken.None);

        batch.Items.Should().HaveCount(3);
        batch.Items.Select(item => item.SourceUrlGroup!.Candidates.Count).Should().Equal(2, 1, 1);
        batch.Items[0].SourceUrlGroup!.Candidates.Select(candidate => candidate.Sequence).Should().Equal(0, 1);
        batch.Items[1].SourceUrlGroup!.Candidates.Should().ContainSingle().Which.Should().BeSameAs(unknown);
        batch.Items[2].SourceUrlGroup!.Candidates.Should().ContainSingle().Which.Should().BeSameAs(otherMessage);
    }

    [Fact]
    public async Task Filters_dropped_attachments_and_returns_retain_manual_and_skip_as_terminal_results()
    {
        var stage = new CandidateCollectionStage(new FakeIdentityFactory(), new FakeHistoryReader());
        var dropped = Attachment("message-1", "pixel.png", [1], "drop");
        var retained = Attachment("message-1", "large.pdf", [2], "retain_only", "B_ATTACHMENT_OVER_5MB_RETAIN");
        var review = Attachment("message-1", "review.pdf", [3], "manual_review", "P0_C_MANUAL_REVIEW");
        var skipped = Attachment("message-1", "skip.pdf", [4], "skip");

        var batch = await stage.ExecuteAsync(
            Scan(new[] { Message("message-1") }, new[] { dropped, retained, review, skipped }, Array.Empty<MailboxUrlCandidate>()),
            CancellationToken.None);

        batch.Items.Should().BeEmpty();
        batch.EffectiveTerminalResults.Select(result => result.Status)
            .Should()
            .Equal(CandidateStatus.Retained, CandidateStatus.ManualReview, CandidateStatus.Duplicate);
        batch.EffectiveTerminalResults.Select(result => result.Failure!.ReasonCode)
            .Should()
            .Equal("B_ATTACHMENT_OVER_5MB_RETAIN", "P0_C_MANUAL_REVIEW", "PREFILTER_SKIP");
    }

    [Fact]
    public async Task Coalesces_run_duplicates_and_omits_candidates_found_in_history()
    {
        var history = new FakeHistoryReader("identity-history.pdf");
        var stage = new CandidateCollectionStage(new FakeIdentityFactory(), history);
        var duplicateA = Attachment("message-1", "same.pdf", [9], "main_chain");
        var duplicateB = Attachment("message-1", "same.pdf", [9], "main_chain");
        var historical = Attachment("message-1", "identity-history.pdf", [8], "main_chain");

        var batch = await stage.ExecuteAsync(
            Scan(new[] { Message("message-1") }, new[] { duplicateA, duplicateB, historical }, Array.Empty<MailboxUrlCandidate>()),
            CancellationToken.None);

        batch.Items.Should().ContainSingle();
        batch.EffectiveTerminalResults.Select(result => result.Failure!.ReasonCode)
            .Should()
            .Equal("CURRENT_RUN_DUPLICATE_SKIP", "HISTORY_DUPLICATE_SKIP");
        batch.Items[0].Candidate.Sequence.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_collecting_candidates()
    {
        var stage = new CandidateCollectionStage(new FakeIdentityFactory(), new FakeHistoryReader());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> act = async () => await stage.ExecuteAsync(
                Scan(new[] { Message("message-1") }, new[] { Attachment("message-1", "a.pdf", [1], "main_chain") }, Array.Empty<MailboxUrlCandidate>()),
                cancellation.Token)
            .ConfigureAwait(false);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static MailboxScanResult Scan(
        IReadOnlyList<MailboxMessage> messages,
        IReadOnlyList<MailboxAttachmentCandidate> attachments,
        IReadOnlyList<MailboxUrlCandidate> urls)
        => new(messages, attachments, 20, "uidvalidity-4", false)
        {
            AccountId = "account-fixture",
            UrlCandidates = urls,
        };

    private static MailboxMessage Message(string uid)
        => new("INBOX", uid, 4, DateTimeOffset.UnixEpoch, "Synthetic", "sender@fixture.invalid", Array.Empty<string>(), false);

    private static MailboxAttachmentCandidate Attachment(
        string uid,
        string fileName,
        byte[] payload,
        string action,
        string reason = "B_ATTACHMENT_MAIN_CHAIN")
        => new(
            "INBOX",
            uid,
            fileName,
            "application/pdf",
            "attachment",
            payload,
            1,
            new AttachmentCandidateDecision("B", action, reason, Array.Empty<string>(), Array.Empty<string>(), null, null));

    private static MailboxUrlCandidate Url(string uid, long sequence, string value)
        => new(
            "account-fixture",
            "INBOX",
            "uidvalidity-4",
            uid,
            new Uri(value),
            "chinatax_direct_invoice",
            "group-hmac-v1-fixture",
            new Dictionary<string, string> { ["invoice_number"] = "12345678901234567890" },
            sequence);

    private sealed class FakeIdentityFactory : ICandidateIdentityFactory
    {
        public Task<DocumentIdentity> CreateAttachmentAsync(
            string accountId,
            string mailbox,
            string uidValidity,
            MailboxAttachmentCandidate attachment,
            CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create($"attachment:{attachment.FileName}"));

        public Task<DocumentIdentity> CreateUrlAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create($"url:{candidate.MessageUid}:{candidate.Sequence}"));

        public Task<DocumentIdentity> CreateUrlGroupAsync(
            string providerFamily,
            IReadOnlyList<MailboxUrlCandidate> candidates,
            CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create(
                $"group:{candidates[0].AccountId}:{candidates[0].Mailbox}:{candidates[0].UidValidity}:{candidates[0].MessageUid}:{providerFamily}:{candidates[0].Sequence}"));

        public Task<DocumentIdentity> CreateRecoveredArtifactAsync(
            DocumentIdentity sourceGroupIdentity,
            InvoiceFlowAI.Application.Url.RecoveredArtifactKind kind,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create($"recovered:{sourceGroupIdentity.Value}:{kind}:{content.Length}"));
    }

    private sealed class FakeHistoryReader(params string[] fileNames) : ICandidateHistoryReader
    {
        private readonly HashSet<string> _known = fileNames
            .Select(name => $"attachment:{name}")
            .ToHashSet(StringComparer.Ordinal);

        public Task<bool> ExistsAsync(DocumentIdentity identity, CancellationToken cancellationToken)
            => Task.FromResult(_known.Contains(identity.Value));
    }
}