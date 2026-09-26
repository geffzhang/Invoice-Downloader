using FluentAssertions;
using System.Text;
using InvoiceFlowAI.Application.Candidates;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Candidates;

public sealed class CandidateIdentityFactoryTests
{
    private static readonly byte[] KeyBytes = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public async Task Secret_store_key_provider_creates_and_reuses_a_versioned_key()
    {
        var store = new FakeSecretStore();
        var provider = new SecretStoreCandidateIdentityKeyProvider(store);

        var first = await provider.GetCurrentKeyAsync(CancellationToken.None);
        var second = await provider.GetCurrentKeyAsync(CancellationToken.None);

        first.Version.Should().Be("1");
        first.KeyBytes.Length.Should().Be(32);
        second.Version.Should().Be(first.Version);
        second.KeyBytes.ToArray().Should().Equal(first.KeyBytes.ToArray());
        store.Values.Should().ContainKey(SecretStoreCandidateIdentityKeyProvider.SecretName);
        store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Attachment_identity_is_stable_and_scoped_to_account_mailbox_uidvalidity_and_message()
    {
        var factory = new CandidateIdentityFactory(new FakeKeyProvider("1", KeyBytes));
        var payload = new byte[] { 1, 2, 3, 4 };
        var attachment = new MailboxAttachmentCandidate(
            "INBOX", "uid-1", "invoice.pdf", "application/pdf", "attachment", payload, 1,
            new AttachmentCandidateDecision("B", "main_chain", "B_ATTACHMENT_MAIN_CHAIN", Array.Empty<string>(), Array.Empty<string>(), null, null));

        var identity = await factory.CreateAttachmentAsync("account-a", "INBOX", "validity-a", attachment, CancellationToken.None);
        var sameIdentity = await factory.CreateAttachmentAsync("account-a", "INBOX", "validity-a", attachment, CancellationToken.None);
        var otherAccount = await factory.CreateAttachmentAsync("account-b", "INBOX", "validity-a", attachment, CancellationToken.None);
        var otherMailbox = await factory.CreateAttachmentAsync("account-a", "Archive", "validity-a", attachment, CancellationToken.None);
        var otherValidity = await factory.CreateAttachmentAsync("account-a", "INBOX", "validity-b", attachment, CancellationToken.None);
        var otherMessage = await factory.CreateAttachmentAsync(
            "account-a", "INBOX", "validity-a", attachment with { MessageUid = "uid-2" }, CancellationToken.None);

        sameIdentity.Should().Be(identity);
        new[] { otherAccount, otherMailbox, otherValidity, otherMessage }.Should().OnlyContain(value => value != identity);
        identity.Value.Should().StartWith("a1_");
    }

    [Fact]
    public async Task Url_identity_hmac_preserves_query_distinctions_ignores_fragment_and_never_exposes_url()
    {
        var factory = new CandidateIdentityFactory(new FakeKeyProvider("2", KeyBytes));
        var candidate = UrlCandidate("https://FIXTURE.invalid/invoice?fileCode=invoice_pdf&sig=secret-token#fragment-a");

        var identity = await factory.CreateUrlAsync(candidate, CancellationToken.None);
        var sameResource = await factory.CreateUrlAsync(
            candidate with { SourceUrl = new Uri("https://fixture.invalid/invoice?fileCode=invoice_pdf&sig=secret-token#other") },
            CancellationToken.None);
        var differentQuery = await factory.CreateUrlAsync(
            candidate with { SourceUrl = new Uri("https://fixture.invalid/invoice?fileCode=invoice_xml&sig=secret-token") },
            CancellationToken.None);

        sameResource.Should().Be(identity);
        differentQuery.Should().NotBe(identity);
        identity.Value.Should().StartWith("u2_");
        identity.Value.Should().NotContain("fixture.invalid");
        identity.Value.Should().NotContain("secret-token");
        identity.Value.Should().NotContain("fileCode");
    }

    [Fact]
    public async Task Url_group_identity_is_stable_scoped_and_opaque()
    {
        var factory = new CandidateIdentityFactory(new FakeKeyProvider("2", KeyBytes));
        var candidate = UrlCandidate("https://fixture.invalid/invoice?sig=secret-token");

        var identity = await factory.CreateUrlGroupAsync("chinatax_direct_invoice", [candidate], CancellationToken.None);
        var sameGroup = await factory.CreateUrlGroupAsync(
            "chinatax_direct_invoice",
            [candidate with { SourceUrl = new Uri("https://fixture.invalid/another-link?sig=other-token") }],
            CancellationToken.None);
        var otherMessage = await factory.CreateUrlGroupAsync(
            "chinatax_direct_invoice",
            [candidate with { MessageUid = "uid-2" }],
            CancellationToken.None);
        var otherProvider = await factory.CreateUrlGroupAsync("baiwang", [candidate], CancellationToken.None);
        var otherAccount = await factory.CreateUrlGroupAsync(
            "chinatax_direct_invoice",
            [candidate with { AccountId = "account-other" }],
            CancellationToken.None);

        sameGroup.Should().Be(identity);
        new[] { otherMessage, otherProvider, otherAccount }.Should().OnlyContain(value => value != identity);
        identity.Value.Should().StartWith("g2_");
        identity.Value.Should().NotContain("fixture.invalid");
        identity.Value.Should().NotContain("secret-token");
        identity.Value.Should().NotContain("chinatax");
    }

    [Fact]
    public async Task Recovered_artifact_identity_is_bound_to_group_kind_and_content_digest()
    {
        var factory = new CandidateIdentityFactory(new FakeKeyProvider("2", KeyBytes));
        var sourceGroup = DocumentIdentity.Create("g2_opaque-source-group");
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nfirst");
        var samePdf = Encoding.ASCII.GetBytes("%PDF-1.7\nfirst");
        var otherPdf = Encoding.ASCII.GetBytes("%PDF-1.7\nsecond");
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />");

        var identity = await factory.CreateRecoveredArtifactAsync(sourceGroup, RecoveredArtifactKind.Pdf, pdf, CancellationToken.None);
        var sameIdentity = await factory.CreateRecoveredArtifactAsync(sourceGroup, RecoveredArtifactKind.Pdf, samePdf, CancellationToken.None);
        var otherContent = await factory.CreateRecoveredArtifactAsync(sourceGroup, RecoveredArtifactKind.Pdf, otherPdf, CancellationToken.None);
        var otherKind = await factory.CreateRecoveredArtifactAsync(sourceGroup, RecoveredArtifactKind.Xml, xml, CancellationToken.None);
        var otherGroup = await factory.CreateRecoveredArtifactAsync(
            DocumentIdentity.Create("g2_another-group"), RecoveredArtifactKind.Pdf, pdf, CancellationToken.None);

        sameIdentity.Should().Be(identity);
        new[] { otherContent, otherKind, otherGroup }.Should().OnlyContain(value => value != identity);
        identity.Value.Should().StartWith("r2_");
        identity.Value.Should().NotContain(sourceGroup.Value);
        identity.Value.Should().NotContain("%PDF");
    }

    private static MailboxUrlCandidate UrlCandidate(string value)
        => new(
            "account-fixture", "INBOX", "validity-1", "uid-1", new Uri(value), "provider", "opaque-group",
            new Dictionary<string, string>(), 0);

    private sealed class FakeKeyProvider(string version, byte[] key) : ICandidateIdentityKeyProvider
    {
        public Task<CandidateIdentityKey> GetCurrentKeyAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CandidateIdentityKey(version, key));
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public int SaveCount { get; private set; }

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[name] = value;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.GetValueOrDefault(name));
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Remove(name);
            return Task.CompletedTask;
        }
    }
}