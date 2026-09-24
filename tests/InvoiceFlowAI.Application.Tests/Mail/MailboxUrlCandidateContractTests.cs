using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Domain.Runs;
using System.Text.Json;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Mail;

public sealed class MailboxUrlCandidateContractTests
{
    [Fact]
    public void Url_candidate_preserves_source_and_structured_recovery_fields()
    {
        var sourceUrl = new Uri("https://fixture.invalid/invoice?fileCode=fixture_pdf&sig=synthetic-token");
        var expectedFields = new Dictionary<string, string>
        {
            ["invoice_number"] = "12345678901234567890",
            ["seller"] = "Synthetic Seller",
            ["invoice_date"] = "2026-09-24",
            ["preferred_kind"] = "pdf",
        };
        var candidate = new MailboxUrlCandidate(
            "account-fixture",
            "INBOX",
            "uidvalidity-42",
            "message-17",
            sourceUrl,
            "kpbyd_direct_invoice",
            "group-hmac-v1-fixture",
            expectedFields,
            3);

        candidate.AccountId.Should().Be("account-fixture");
        candidate.Mailbox.Should().Be("INBOX");
        candidate.UidValidity.Should().Be("uidvalidity-42");
        candidate.MessageUid.Should().Be("message-17");
        candidate.SourceUrl.Should().BeSameAs(sourceUrl);
        candidate.SourceUrl.Query.Should().Be("?fileCode=fixture_pdf&sig=synthetic-token");
        candidate.ProviderFamily.Should().Be("kpbyd_direct_invoice");
        candidate.ProviderGroupId.Should().Be("group-hmac-v1-fixture");
        candidate.ExpectedFields.Should().BeEquivalentTo(expectedFields);
        candidate.Sequence.Should().Be(3);
    }

    [Fact]
    public void Existing_mailbox_scan_result_constructor_defaults_url_candidates_to_empty()
    {
        var result = new MailboxScanResult(
            Array.Empty<MailboxMessage>(),
            Array.Empty<MailboxAttachmentCandidate>(),
            17,
            "uidvalidity-42",
            false);

        result.UrlCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Python_golden_fixture_covers_candidate_provider_and_pairing_contracts_without_sensitive_outputs()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "CandidateUrlPairing",
            "python-golden.v1.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = fixture.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();

        cases.Select(item => item.GetProperty("id").GetString()).Should().Contain(new[]
        {
            "link-plain-text",
            "link-html-anchor",
            "link-body-html-duplicate-order",
            "role-unrecognized",
            "pair-matched",
            "pair-unmatched",
            "pair-ambiguous",
        });

        var expectedFamilies = new[]
        {
            "chinatax_direct_invoice",
            "bwjf_signed_invoice",
            "fpyun_direct_invoice",
            "nuonuo_scan_invoice",
            "pdd_direct_invoice",
            "jdcloud_direct_invoice",
            "kpbyd_direct_invoice",
            "baiwang",
        };
        cases
            .Where(item => item.GetProperty("kind").GetString() == "provider_fields")
            .Select(item => item.GetProperty("expected").GetProperty("providerFamily").GetString())
            .Should()
            .Contain(expectedFamilies);

        cases
            .Where(item => item.GetProperty("kind").GetString() == "pairing_projection")
            .Select(item => item.GetProperty("expected").GetProperty("role").GetString())
            .Should()
            .Contain(new[] { "RideInvoice", "RideItinerary", "HotelInvoice", "HotelFolio", "none" });

        var expectedJson = JsonSerializer.Serialize(cases.Select(item => item.GetProperty("expected")).ToArray());
        expectedJson.Should().NotContain("fixture.invalid", "expected output must not reveal source URLs");
        expectedJson.Should().NotContain("bodyText");
        expectedJson.Should().NotContain("htmlBody");
        expectedJson.Should().NotContain("queryToken");
        expectedJson.Should().NotContain("authorization");

        var allowedExpectedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "invoice_number",
            "seller",
            "invoice_date",
            "preferred_kind",
        };
        foreach (var item in cases.Where(item => item.GetProperty("kind").GetString() == "provider_fields"))
        {
            item.GetProperty("expected").GetProperty("expectedFields")
                .EnumerateObject()
                .Should()
                .OnlyContain(field => allowedExpectedFields.Contains(field.Name));
        }

        foreach (var item in cases)
        {
            item.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Object);
            item.GetProperty("expected").ValueKind.Should().Be(JsonValueKind.Object);
        }
    }
}