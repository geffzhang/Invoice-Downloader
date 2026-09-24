using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Pipeline;

public sealed class CandidateCollectionStage : ICandidateCollectionStage
{
    private readonly ICandidateIdentityFactory _identityFactory;
    private readonly ICandidateHistoryReader _historyReader;

    public CandidateCollectionStage(
        ICandidateIdentityFactory identityFactory,
        ICandidateHistoryReader historyReader)
    {
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _historyReader = historyReader ?? throw new ArgumentNullException(nameof(historyReader));
    }

    public async Task<CandidateBatch> ExecuteAsync(MailboxScanResult input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var orderedSources = OrderSources(input);
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<CandidateWorkItem>();
        var terminalResults = new List<CandidateProcessResult>();
        var sequence = 0L;

        foreach (var source in orderedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Attachment is { Decision.Action: "drop" })
            {
                continue;
            }

            var urlGroup = source.UrlCandidates is { } urlCandidates
                ? await CreateUrlGroupAsync(urlCandidates, cancellationToken).ConfigureAwait(false)
                : null;
            var identity = source.Attachment is { } attachment
                ? await _identityFactory.CreateAttachmentAsync(
                    input.AccountId,
                    attachment.Mailbox,
                    input.UidValidity,
                    attachment,
                    cancellationToken).ConfigureAwait(false)
                : urlGroup!.GroupIdentity;

            var candidateSequence = sequence++;
            var candidate = CreateCandidate(input, source, identity, candidateSequence);
            if (!seenIdentities.Add(identity.Value))
            {
                terminalResults.Add(Terminal(candidate, CandidateStatus.Duplicate, "CURRENT_RUN_DUPLICATE_SKIP"));
                continue;
            }

            if (await _historyReader.ExistsAsync(identity, cancellationToken).ConfigureAwait(false))
            {
                terminalResults.Add(Terminal(candidate, CandidateStatus.Duplicate, "HISTORY_DUPLICATE_SKIP"));
                continue;
            }

            if (source.Attachment is { } attachmentSource)
            {
                var decision = attachmentSource.Decision;
                switch (decision.Action)
                {
                    case "retain_only":
                        terminalResults.Add(Terminal(candidate, CandidateStatus.Retained, decision.ReasonCode));
                        continue;
                    case "manual_review":
                        terminalResults.Add(Terminal(candidate, CandidateStatus.ManualReview, decision.ReasonCode));
                        continue;
                    case "skip":
                        terminalResults.Add(Terminal(candidate, CandidateStatus.Duplicate, "PREFILTER_SKIP"));
                        continue;
                    case "main_chain":
                        items.Add(new CandidateWorkItem(candidate, attachmentSource.Payload, attachmentSource));
                        continue;
                    default:
                        terminalResults.Add(Terminal(candidate, CandidateStatus.ManualReview, "UNKNOWN_CANDIDATE_ACTION"));
                        continue;
                }
            }

            items.Add(new CandidateWorkItem(
                candidate,
                ReadOnlyMemory<byte>.Empty,
                SourceUrlCandidate: source.UrlCandidates![0],
                SourceUrlGroup: urlGroup));
        }

        return new CandidateBatch(items, terminalResults);
    }

    private static IReadOnlyList<CandidateSource> OrderSources(MailboxScanResult input)
    {
        var messageOrder = input.Messages
            .Select((message, index) => (message.Uid, index))
            .GroupBy(static pair => pair.Uid, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().index, StringComparer.Ordinal);
        var sources = new List<CandidateSource>(input.Attachments.Count + input.UrlCandidates.Count);

        for (var index = 0; index < input.Attachments.Count; index++)
        {
            var attachment = input.Attachments[index];
            var order = messageOrder.GetValueOrDefault(attachment.MessageUid, int.MaxValue);
            sources.Add(new CandidateSource(order, 0, index, attachment, null));
        }

        var orderedUrls = input.UrlCandidates
            .Select((candidate, index) => (Candidate: candidate, Index: index,
                MessageOrder: messageOrder.GetValueOrDefault(candidate.MessageUid, int.MaxValue)))
            .OrderBy(static item => item.MessageOrder)
            .ThenBy(static item => item.Candidate.Sequence)
            .ThenBy(static item => item.Index)
            .ToArray();
        var urlGroups = new List<(int MessageOrder, long Sequence, int StableOrder, IReadOnlyList<MailboxUrlCandidate> Candidates)>();
        var groupIndexes = new Dictionary<(string AccountId, string Mailbox, string UidValidity, string MessageUid, string ProviderFamily), int>();
        for (var index = 0; index < orderedUrls.Length; index++)
        {
            var entry = orderedUrls[index];
            var urlCandidate = entry.Candidate;
            var key = (urlCandidate.AccountId, urlCandidate.Mailbox, urlCandidate.UidValidity,
                urlCandidate.MessageUid, urlCandidate.ProviderFamily);
            var groupIndex = string.IsNullOrEmpty(urlCandidate.ProviderFamily)
                ? -1
                : groupIndexes.GetValueOrDefault(key, -1);

            if (groupIndex < 0)
            {
                groupIndex = urlGroups.Count;
                urlGroups.Add((entry.MessageOrder, urlCandidate.Sequence, entry.Index, new[] { urlCandidate }));
                if (!string.IsNullOrEmpty(urlCandidate.ProviderFamily)) groupIndexes.Add(key, groupIndex);
            }
            else
            {
                var existing = urlGroups[groupIndex];
                if (!existing.Candidates.Any(candidate =>
                        candidate.SourceUrl.AbsoluteUri.Equals(urlCandidate.SourceUrl.AbsoluteUri, StringComparison.Ordinal)))
                {
                    urlGroups[groupIndex] = (existing.MessageOrder, existing.Sequence, existing.StableOrder,
                        existing.Candidates.Append(urlCandidate).ToArray());
                }
            }
        }

        foreach (var group in urlGroups)
        {
            sources.Add(new CandidateSource(group.MessageOrder, 1, group.Sequence, null, group.Candidates, group.StableOrder));
        }

        return sources
            .OrderBy(static source => source.MessageOrder)
            .ThenBy(static source => source.KindOrder)
            .ThenBy(static source => source.ItemOrder)
            .ThenBy(static source => source.StableOrder)
            .ToArray();
    }

    private async Task<UrlCandidateGroup> CreateUrlGroupAsync(
        IReadOnlyList<MailboxUrlCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var providerFamily = candidates[0].ProviderFamily;
        var groupIdentity = await _identityFactory.CreateUrlGroupAsync(providerFamily, candidates, cancellationToken)
            .ConfigureAwait(false);
        var evidence = new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(StringComparer.Ordinal);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var fieldEvidence = new Dictionary<string, List<ExpectedFieldEvidence>>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            foreach (var entry in candidate.ExpectedFieldEvidence)
            {
                if (!fieldEvidence.TryGetValue(entry.Key, out var values))
                {
                    values = [];
                    fieldEvidence.Add(entry.Key, values);
                }

                foreach (var item in entry.Value)
                {
                    if (!values.Contains(item)) values.Add(item);
                }
            }

            foreach (var entry in candidate.ExpectedFields)
            {
                if (!candidate.ExpectedFieldEvidence.ContainsKey(entry.Key))
                {
                    if (!fieldEvidence.TryGetValue(entry.Key, out var values))
                    {
                        values = [];
                        fieldEvidence.Add(entry.Key, values);
                    }
                    values.Add(new ExpectedFieldEvidence(entry.Value, ExpectedFieldSource.Body, candidate.Sequence));
                }
            }
        }

        foreach (var entry in fieldEvidence)
        {
            var ordered = entry.Value
                .OrderBy(static item => item.Source)
                .ThenBy(static item => item.DiscoveryOrder)
                .ToArray();
            evidence.Add(entry.Key, ordered);
            if (ordered.Length > 0) fields.Add(entry.Key, ordered[0].Value);
        }

        return new UrlCandidateGroup(providerFamily, candidates, fields, evidence, groupIdentity);
    }

    private static DocumentCandidate CreateCandidate(
        MailboxScanResult scan,
        CandidateSource source,
        DocumentIdentity identity,
        long sequence)
    {
        if (source.Attachment is { } attachment)
        {
            return new DocumentCandidate(
                identity,
                sequence,
                $"candidate-{sequence}",
                attachment.MessageUid,
                attachment.FileName,
                attachment.ContentType,
                attachment.Payload.Length,
                0,
                "mime_attachment",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mailbox"] = attachment.Mailbox,
                    ["candidate_action"] = attachment.Decision.Action,
                    ["prefilter_reason_code"] = attachment.Decision.ReasonCode,
                });
        }

        var url = source.UrlCandidates![0];
        var extension = url.ExpectedFields.TryGetValue("preferred_kind", out var kind)
            && kind is "pdf" or "xml" or "ofd"
                ? $".{kind}"
                : ".pdf";
        return new DocumentCandidate(
            identity,
            sequence,
            $"candidate-{sequence}",
            url.MessageUid,
            $"invoice-url{extension}",
            "application/octet-stream",
            0,
            0,
            "url",
            url.SourceUrl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mailbox"] = url.Mailbox,
                ["provider_family"] = url.ProviderFamily,
                ["provider_group_id"] = url.ProviderGroupId,
            });
    }

    private static CandidateProcessResult Terminal(
        DocumentCandidate candidate,
        CandidateStatus status,
        string reasonCode)
    {
        var message = status switch
        {
            CandidateStatus.Retained => "The candidate was retained for evidence.",
            CandidateStatus.Duplicate => "The candidate was already processed.",
            _ => "The candidate requires manual review.",
        };
        return new CandidateProcessResult(
            candidate,
            status,
            Failure: new CandidateFailure(
                reasonCode,
                FailureScope.Candidate,
                status == CandidateStatus.Duplicate ? FailureCategory.Validation : FailureCategory.Input,
                Retryable: false,
                SafeMessage: message));
    }

    private sealed record CandidateSource(
        int MessageOrder,
        int KindOrder,
        long ItemOrder,
        MailboxAttachmentCandidate? Attachment,
        IReadOnlyList<MailboxUrlCandidate>? UrlCandidates,
        int StableOrder = 0);
}