using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using FluentAssertions;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Parsers;
using InvoiceFlowAI.Infrastructure.Url;
using InvoiceFlowAI.Infrastructure.Mail;
using Xunit;
using MailKit;

namespace InvoiceFlowAI.Infrastructure.Tests.DesktopParity;

public sealed class DesktopParityFixtureTests
{
    [Fact]
    public void Pairing_golden_fixtures_match_python_desktop_decisions()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "pairing.json");
        var fixtures = JsonSerializer.Deserialize<PairingFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Pairing parity fixtures are empty.");
        var engine = new PairingEngine();

        foreach (var fixture in fixtures.Cases)
        {
            var family = Enum.Parse<PairingFamily>(fixture.Family, ignoreCase: true);
            var invoices = fixture.Invoices.Select(document => document.ToDomain(PairingRoleFor(fixture.Family, invoice: true))).ToArray();
            var companions = fixture.Companions.Select(document => document.ToDomain(PairingRoleFor(fixture.Family, invoice: false))).ToArray();
            var result = engine.Pair(family, invoices, companions);
            var shuffled = engine.Pair(family, invoices.Reverse().ToArray(), companions.Reverse().ToArray());

            var pairs = result.Pairs.Select(pair => new[] { pair.Invoice.Id, pair.Companion.Id }).ToArray();
            var shuffledPairs = shuffled.Pairs.Select(pair => new[] { pair.Invoice.Id, pair.Companion.Id }).ToArray();
            pairs.Should().BeEquivalentTo(fixture.ExpectedPairs, options => options.WithStrictOrdering(), fixture.CaseId);
            shuffledPairs.Should().BeEquivalentTo(fixture.ExpectedPairs, options => options.WithStrictOrdering(), fixture.CaseId);
            pairs.Select(pair => pair[0]).Should().OnlyHaveUniqueItems(fixture.CaseId);
            pairs.Select(pair => pair[1]).Should().OnlyHaveUniqueItems(fixture.CaseId);
            result.Ambiguities.Should().HaveCount(fixture.ExpectedAmbiguityCount, fixture.CaseId);
            shuffled.Ambiguities.Should().HaveCount(fixture.ExpectedAmbiguityCount, fixture.CaseId);
        }
    }

    [Fact]
    public void Provider_golden_fixtures_match_detection_and_field_projection_without_echoing_source_content()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "providers.json");
        var fixtures = JsonSerializer.Deserialize<ProviderFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Provider parity fixtures are empty.");

        foreach (var fixture in fixtures.Cases)
        {
            var url = new Uri(fixture.Url);
            var family = MailboxUrlCandidateDiscovery.DetectProviderFamily(url, null, fixture.Subject);
            var fields = MailboxUrlCandidateDiscovery.ExtractExpectedFields(family, url, fixture.Subject, fixture.Body);

            family.Should().Be(fixture.ExpectedFamily, fixture.CaseId);
            fields.Should().BeEquivalentTo(fixture.ExpectedFields, fixture.CaseId);
            fields.Values.Should().NotContain(value =>
                value.Contains(url.Host, StringComparison.OrdinalIgnoreCase)
                || value.Contains("Synthetic Seller为您", StringComparison.Ordinal), fixture.CaseId);
        }
    }

    [Fact]
    public async Task Extraction_golden_fixtures_match_core_invoice_fields()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "extraction.json");
        var fixtures = JsonSerializer.Deserialize<ExtractionFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Extraction parity fixtures are empty.");
        var parser = new XmlInvoiceParser();

        foreach (var fixture in fixtures.Cases)
        {
            var identity = DocumentIdentity.Create(fixture.CaseId);
            var content = Encoding.UTF8.GetBytes(fixture.Xml);
            var candidate = new DocumentCandidate(identity, 1, "correlation-fixture", "uid-fixture",
                "invoice.xml", "application/xml", content.Length, 0, "xml");
            var outcome = await parser.ParseAsync(new ParserWorkItem(candidate, identity.Value, "xml", content), CancellationToken.None);

            outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved, fixture.CaseId);
            outcome.Failure.Should().BeNull(fixture.CaseId);
            var invoice = outcome.Invoice ?? throw new InvalidDataException($"Fixture '{fixture.CaseId}' produced no invoice.");
            invoice.InvoiceDate.Should().Be(DateOnly.Parse(fixture.Expected.InvoiceDate));
            invoice.Purchaser.Should().Be(fixture.Expected.Purchaser);
            invoice.Seller.Should().Be(fixture.Expected.Seller);
            invoice.Amount.Should().Be(decimal.Parse(fixture.Expected.Amount, System.Globalization.CultureInfo.InvariantCulture));
            invoice.TaxAmount.Should().Be(decimal.Parse(fixture.Expected.TaxAmount, System.Globalization.CultureInfo.InvariantCulture));
            invoice.TotalAmount.Should().Be(decimal.Parse(fixture.Expected.TotalAmount, System.Globalization.CultureInfo.InvariantCulture));
            invoice.InvoiceNumber.Should().Be(fixture.Expected.InvoiceNumber);
            invoice.DocumentType.ToString().Should().Be(fixture.Expected.DocumentType);
        }
    }

    [Fact]
    public async Task Xml_layout_and_partial_field_fixtures_match_python_projection()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "xml-extraction.json");
        var fixtures = JsonSerializer.Deserialize<XmlExtractionFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("XML extraction parity fixtures are empty.");
        var parser = new XmlInvoiceParser();

        foreach (var fixture in fixtures.Cases)
        {
            var identity = DocumentIdentity.Create(fixture.CaseId);
            var content = Encoding.UTF8.GetBytes(fixture.Xml);
            var candidate = new DocumentCandidate(identity, 1, "correlation-fixture", "uid-fixture",
                "invoice.xml", "application/xml", content.Length, 0, "xml");
            var outcome = await parser.ParseAsync(new ParserWorkItem(candidate, identity.Value, "xml", content), CancellationToken.None);

            outcome.Disposition.ToString().Should().Be(fixture.ExpectedCSharpDisposition, fixture.CaseId);
            outcome.MissingFields.Should().Equal(fixture.ExpectedCSharpMissingFields, fixture.CaseId);
            var invoice = outcome.Invoice ?? throw new InvalidDataException($"Fixture '{fixture.CaseId}' produced no invoice.");
            invoice.InvoiceNumber.Should().Be(fixture.Expected.InvoiceNumber, fixture.CaseId);
            invoice.InvoiceDate?.ToString("yyyy-MM-dd").Should().Be(fixture.Expected.InvoiceDate, fixture.CaseId);
            invoice.Purchaser.Should().Be(fixture.Expected.Purchaser, fixture.CaseId);
            invoice.Seller.Should().Be(fixture.Expected.Seller, fixture.CaseId);
            invoice.Amount?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Should().Be(fixture.Expected.Amount, fixture.CaseId);
            invoice.TaxAmount?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Should().Be(fixture.Expected.TaxAmount == "" ? null : fixture.Expected.TaxAmount, fixture.CaseId);
            invoice.TotalAmount?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Should().Be(fixture.Expected.TotalAmount == "" ? null : fixture.Expected.TotalAmount, fixture.CaseId);
        }
    }

    [Fact]
    public void Mailbox_uid_golden_fixtures_are_stably_deduplicated_and_ordered()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "mailbox-order.json");
        var fixtures = JsonSerializer.Deserialize<MailboxOrderFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Mailbox order parity fixtures are empty.");

        foreach (var fixture in fixtures.Cases)
        {
            var input = fixture.InputUids.Select(uid => new UniqueId((uint)uid)).ToArray();
            var output = MailKitMailboxSession.FilterUidsAfterCursor(input, fixture.SinceUid);

            output.Select(uid => (int)uid.Id).Should().Equal(fixture.ExpectedUids, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Candidate_identity_golden_fixtures_are_opaque_stable_and_retain_malformed_urls()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "candidate-identity.json");
        var fixtures = JsonSerializer.Deserialize<CandidateIdentityFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Candidate identity parity fixtures are empty.");
        var keyProvider = new FixtureIdentityKeyProvider();
        var identityFactory = new CandidateIdentityFactory(keyProvider);

        foreach (var fixture in fixtures.Cases)
        {
            var candidates = fixture.Urls.Select((url, index) => new MailboxUrlCandidate(
                fixture.AccountId,
                fixture.Mailbox,
                fixture.UidValidity,
                fixture.MessageUid,
                new Uri(url, UriKind.RelativeOrAbsolute),
                fixture.ProviderFamily,
                "opaque-group",
                new Dictionary<string, string>(),
                index)).ToArray();
            var identity = await identityFactory.CreateUrlGroupAsync(fixture.ProviderFamily, candidates, CancellationToken.None);

            identity.Value.Should().StartWith(fixture.ExpectedIdentityPrefix, fixture.CaseId);
            fixture.MustNotContain.Should().OnlyContain(value => !identity.Value.Contains(value, StringComparison.Ordinal), fixture.CaseId);
            if (fixture.ExpectedStatus is null)
            {
                var reversed = await identityFactory.CreateUrlGroupAsync(fixture.ProviderFamily, candidates.Reverse().ToArray(), CancellationToken.None);
                reversed.Should().Be(identity, fixture.CaseId);
                continue;
            }

            var scan = new MailboxScanResult(
                [new MailboxMessage(fixture.Mailbox, fixture.MessageUid, 1, DateTimeOffset.UnixEpoch, "Synthetic", "sender@fixture.invalid", [], false)],
                [], 1, fixture.UidValidity, false)
            {
                AccountId = fixture.AccountId,
                UrlCandidates = candidates,
            };
            var stage = new CandidateCollectionStage(identityFactory, new FixtureHistoryReader());
            var batch = await stage.ExecuteAsync(scan, CancellationToken.None);

            batch.Items.Should().BeEmpty(fixture.CaseId);
            var result = batch.EffectiveTerminalResults.Should().ContainSingle(fixture.CaseId).Which;
            result.Status.ToString().Should().Be(fixture.ExpectedStatus, fixture.CaseId);
            result.Failure!.ReasonCode.Should().Be(fixture.ExpectedReasonCode, fixture.CaseId);
            result.Candidate.SourceUrl.Should().BeNull(fixture.CaseId);
            fixture.MustNotContain.Should().OnlyContain(value => !result.Candidate.DocumentId.Value.Contains(value, StringComparison.Ordinal), fixture.CaseId);
        }
    }

    [Fact]
    public async Task Candidate_order_golden_fixtures_preserve_mailbox_and_source_order()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "candidate-order.json");
        var fixtures = JsonSerializer.Deserialize<CandidateOrderFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Candidate order parity fixtures are empty.");

        foreach (var fixture in fixtures.Cases)
        {
            var attachments = fixture.Attachments.Select(item => new MailboxAttachmentCandidate(
                "INBOX", item.Uid, item.FileName, "application/pdf", "attachment", new byte[] { 1 }, 1,
                new AttachmentCandidateDecision("B", "main_chain", "B_ATTACHMENT_MAIN_CHAIN", [], [], null, null))).ToArray();
            var urls = fixture.UrlCandidates.Select(item => new MailboxUrlCandidate(
                "account-fixture", "INBOX", "validity-fixture", item.Uid,
                new Uri($"https://provider.fixture.invalid/download/{item.Uid}/{item.Sequence}"),
                "unknown_provider", string.Empty, new Dictionary<string, string>(), item.Sequence)).ToArray();
            var messages = fixture.Messages.Select(uid => new MailboxMessage(
                "INBOX", uid, 1, DateTimeOffset.UnixEpoch, "Synthetic", "sender@fixture.invalid", [], false)).ToArray();
            var scan = new MailboxScanResult(messages, attachments, 2, "validity-fixture", false)
            {
                AccountId = "account-fixture",
                UrlCandidates = urls,
            };
            var stage = new CandidateCollectionStage(new FixtureIdentityFactory(), new FixtureHistoryReader());

            var batch = await stage.ExecuteAsync(scan, CancellationToken.None);

            batch.Items.Select(item => item.Candidate.OriginalFileName).Should().Equal(fixture.ExpectedCandidateNames, fixture.CaseId);
            batch.Items.Select(item => item.Candidate.Sequence).Should().Equal(fixture.ExpectedSequences.Select(value => (long)value), fixture.CaseId);
        }
    }

    [Fact]
    public void Provider_recovery_golden_fixtures_select_the_matching_safe_artifact()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "provider-recovery.json");
        var fixtures = JsonSerializer.Deserialize<ProviderRecoveryFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Provider recovery parity fixtures are empty.");

        foreach (var fixture in fixtures.Cases)
        {
            var artifacts = fixture.Captures.Select(capture => CreateArtifact(capture)).ToArray();
            var result = BaiwangArtifactSelector.Select(fixture.ExpectedFields, artifacts);

            result.SelectedArtifactIndex.Should().Be(fixture.ExpectedSelectedIndex, fixture.CaseId);
            result.SelectedArtifact.Should().NotBeNull(fixture.CaseId);
            result.SelectedArtifact!.Kind.ToString().Should().Be(fixture.ExpectedSelectedKind, fixture.CaseId);
            result.SelectedArtifact.SourceUrlOrdinal.Should().Be(fixture.ExpectedSourceUrlOrdinal, fixture.CaseId);
            result.SelectedArtifact.MatchReasonCode.Should().Be(fixture.ExpectedMatchReason, fixture.CaseId);
            result.Artifacts.Should().Contain(artifact =>
                artifact.MatchReasonCode == "wrapper_detected" && artifact.ExpectedMatch == fixture.ExpectedWrapperMatch,
                fixture.CaseId);
        }
    }

    private static CapturedUrlArtifact CreateArtifact(ProviderRecoveryCapture capture)
    {
        var content = capture.Kind switch
        {
            "Pdf" => Encoding.ASCII.GetBytes("%PDF-1.7 synthetic"),
            "Xml" => Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />"),
            "Ofd" => Encoding.UTF8.GetBytes("synthetic-ofd"),
            _ => throw new InvalidDataException($"Unsupported artifact kind '{capture.Kind}'."),
        };
        return new CapturedUrlArtifact(
            Enum.Parse<InvoiceFlowAI.Application.Url.RecoveredArtifactKind>(capture.Kind, ignoreCase: true),
            "application/" + capture.Kind.ToLowerInvariant(),
            content,
            capture.SourceUrlOrdinal,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            "https://provider.fixture.invalid",
            capture.Fields,
            null,
            capture.CaptureReason);
    }

    private static PairingRole PairingRoleFor(string family, bool invoice) =>
        (family.ToLowerInvariant(), invoice) switch
        {
            ("ride", true) => PairingRole.RideInvoice,
            ("ride", false) => PairingRole.RideItinerary,
            ("hotel", true) => PairingRole.HotelInvoice,
            ("hotel", false) => PairingRole.HotelFolio,
            _ => throw new InvalidDataException($"Unsupported fixture family '{family}'."),
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record PairingFixtureSet(IReadOnlyList<PairingCase> Cases);

    private sealed record PairingCase(
        string CaseId,
        string Family,
        IReadOnlyList<PairingDocumentFixture> Invoices,
        IReadOnlyList<PairingDocumentFixture> Companions,
        IReadOnlyList<string[]> ExpectedPairs,
        int ExpectedAmbiguityCount);

    private sealed record PairingDocumentFixture(
        string Id,
        decimal? Amount,
        DateOnly? BusinessDate,
        string Provider,
        IReadOnlyList<string> MerchantTokens,
        string SourceMessageUid)
    {
        public PairingDocument ToDomain(PairingRole role) => new(
            Id,
            role,
            Amount,
            BusinessDate,
            Provider,
            MerchantTokens,
            SourceMessageUid,
            string.Empty);
            }

    private sealed record ProviderFixtureSet(IReadOnlyList<ProviderCase> Cases);

    private sealed record ProviderCase(
        string CaseId,
        string Url,
        string Subject,
        string Body,
        string ExpectedFamily,
        IReadOnlyDictionary<string, string> ExpectedFields);

    private sealed record ExtractionFixtureSet(IReadOnlyList<ExtractionCase> Cases);

    private sealed record ExtractionCase(string CaseId, string Xml, ExtractionExpected Expected);

    private sealed record ExtractionExpected(
        string InvoiceDate,
        string Purchaser,
        string Seller,
        string Amount,
        string TaxAmount,
        string TotalAmount,
        string InvoiceNumber,
        string DocumentType);

    private sealed record XmlExtractionFixtureSet(IReadOnlyList<XmlExtractionCase> Cases);

    private sealed record XmlExtractionCase(
        string CaseId,
        string Xml,
        XmlExtractionExpected Expected,
        string ExpectedCSharpDisposition,
        IReadOnlyList<string> ExpectedCSharpMissingFields);

    private sealed record XmlExtractionExpected(
        string InvoiceNumber,
        string InvoiceDate,
        string Purchaser,
        string Seller,
        string Amount,
        string TaxAmount,
        string TotalAmount);

    private sealed record MailboxOrderFixtureSet(IReadOnlyList<MailboxOrderCase> Cases);

    private sealed record MailboxOrderCase(string CaseId, long SinceUid, IReadOnlyList<int> InputUids, IReadOnlyList<int> ExpectedUids);

    private sealed record ProviderRecoveryFixtureSet(IReadOnlyList<ProviderRecoveryCase> Cases);

    private sealed record ProviderRecoveryCase(
        string CaseId,
        IReadOnlyDictionary<string, string> ExpectedFields,
        IReadOnlyList<ProviderRecoveryCapture> Captures,
        int ExpectedSelectedIndex,
        string ExpectedSelectedKind,
        int ExpectedSourceUrlOrdinal,
        string ExpectedMatchReason,
        bool ExpectedWrapperMatch);

    private sealed record ProviderRecoveryCapture(
        string Kind,
        int SourceUrlOrdinal,
        IReadOnlyDictionary<string, string> Fields,
        string CaptureReason);

    private sealed record CandidateOrderFixtureSet(IReadOnlyList<CandidateOrderCase> Cases);

    private sealed record CandidateOrderCase(
        string CaseId,
        IReadOnlyList<string> Messages,
        IReadOnlyList<CandidateOrderAttachment> Attachments,
        IReadOnlyList<CandidateOrderUrl> UrlCandidates,
        IReadOnlyList<string> ExpectedCandidateNames,
        IReadOnlyList<int> ExpectedSequences);

    private sealed record CandidateOrderAttachment(string Uid, string FileName);

    private sealed record CandidateOrderUrl(string Uid, long Sequence);

    private sealed record CandidateIdentityFixtureSet(IReadOnlyList<CandidateIdentityCase> Cases);

    private sealed record CandidateIdentityCase(
        string CaseId,
        string AccountId,
        string Mailbox,
        string UidValidity,
        string MessageUid,
        string ProviderFamily,
        IReadOnlyList<string> Urls,
        string ExpectedIdentityPrefix,
        IReadOnlyList<string> MustNotContain,
        string? ExpectedStatus = null,
        string? ExpectedReasonCode = null);

    private sealed class FixtureIdentityKeyProvider : ICandidateIdentityKeyProvider
    {
        private readonly byte[] _key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

        public Task<CandidateIdentityKey> GetCurrentKeyAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CandidateIdentityKey("2", _key));
    }

    private sealed class FixtureIdentityFactory : ICandidateIdentityFactory
    {
        public Task<DocumentIdentity> CreateAttachmentAsync(string accountId, string mailbox, string uidValidity,
            MailboxAttachmentCandidate attachment, CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create($"attachment:{attachment.MessageUid}:{attachment.FileName}"));

        public Task<DocumentIdentity> CreateUrlAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create($"url:{candidate.MessageUid}:{candidate.Sequence}"));

        public Task<DocumentIdentity> CreateUrlGroupAsync(string providerFamily, IReadOnlyList<MailboxUrlCandidate> candidates,
            CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create($"group:{candidates[0].MessageUid}:{candidates[0].Sequence}"));

        public Task<DocumentIdentity> CreateRecoveredArtifactAsync(DocumentIdentity sourceGroupIdentity,
            RecoveredArtifactKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
            => Task.FromResult(DocumentIdentity.Create($"recovered:{sourceGroupIdentity.Value}:{kind}"));
    }

    private sealed class FixtureHistoryReader : ICandidateHistoryReader
    {
        public Task<bool> ExistsAsync(DocumentIdentity identity, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
