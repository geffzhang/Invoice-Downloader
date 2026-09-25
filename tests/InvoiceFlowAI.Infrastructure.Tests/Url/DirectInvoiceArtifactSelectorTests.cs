using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class DirectInvoiceArtifactSelectorTests
{
    [Fact]
    public void Selects_matching_pdf_from_multiple_captures_and_preserves_inventory()
    {
        var wrongPdf = Artifact(RecoveredArtifactKind.Pdf, "invoice_number", "11111111111111111110");
        var matchingXml = Artifact(RecoveredArtifactKind.Xml, "invoice_number", "11111111111111111111");
        var matchingPdf = Artifact(RecoveredArtifactKind.Pdf, "invoice_number", "11111111111111111111");

        var result = DirectInvoiceArtifactSelector.Select(
            new Dictionary<string, string> { ["invoice_number"] = "11111111111111111111" },
            [wrongPdf, matchingXml, matchingPdf]);

        result.Artifacts.Select(artifact => artifact.Kind)
            .Should().Equal(RecoveredArtifactKind.Pdf, RecoveredArtifactKind.Xml, RecoveredArtifactKind.Pdf);
        result.Artifacts[0].Content.ToArray().Should().Equal(wrongPdf.Content.ToArray());
        result.Artifacts[1].Content.ToArray().Should().Equal(matchingXml.Content.ToArray());
        result.SelectedArtifact!.Content.ToArray().Should().Equal(matchingPdf.Content.ToArray());
        result.SelectedArtifact!.ExpectedMatch.Should().BeTrue();
        result.SelectedArtifact.MatchReasonCode.Should().Be("invoice_number");
    }

    [Fact]
    public void Shared_artifact_kind_fixtures_match_direct_selector_contract()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "direct-artifact-selection.json");
        var fixtures = System.Text.Json.JsonSerializer.Deserialize<DirectSelectionFixtureSet>(
            File.ReadAllText(fixturePath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Direct artifact selection fixtures are empty.");

        foreach (var fixture in fixtures.Cases)
        {
            var captures = fixture.Artifacts.Select(item => Artifact(
                Enum.Parse<RecoveredArtifactKind>(item.Kind, ignoreCase: true), item.Fields)).ToArray();
            if (fixture.ExpectedFailure.Length > 0)
            {
                var act = () => DirectInvoiceArtifactSelector.Select(fixture.ExpectedFields, captures);
                act.Should().Throw<UrlRecoveryException>()
                    .Which.ReasonCode.Should().Be(fixture.ExpectedFailure, fixture.CaseId);
                continue;
            }

            var result = DirectInvoiceArtifactSelector.Select(fixture.ExpectedFields, captures);
            result.SelectedArtifactIndex.Should().Be(fixture.ExpectedSelectedIndex, fixture.CaseId);
            result.SelectedArtifact!.Kind.ToString().Should().Be(fixture.ExpectedSelectedKind, fixture.CaseId);
            result.SelectedArtifact.MatchReasonCode.Should().Be(fixture.ExpectedMatchReason, fixture.CaseId);
        }
    }

    [Fact]
    public void Allows_only_one_xml_without_conflicting_expected_fields()
    {
        var xml = Artifact(RecoveredArtifactKind.Xml, "seller", "Acme Ltd");

        var result = DirectInvoiceArtifactSelector.Select(
            new Dictionary<string, string>(), [xml]);

        result.SelectedArtifact!.Content.ToArray().Should().Equal(xml.Content.ToArray());
        result.SelectedArtifact!.MatchReasonCode.Should().Be("single_xml_without_conflict");
    }

    [Fact]
    public void Selects_pdf_when_expected_invoice_number_appears_only_in_source_url()
    {
        const string invoiceNumber = "11111111111111111111";
        var pdf = Artifact(RecoveredArtifactKind.Pdf, "seller", "Acme Ltd");
        var sourceUrl = new Uri($"https://files.example/invoice/{invoiceNumber}.pdf?token=private");

        var result = DirectInvoiceArtifactSelector.Select(
            new Dictionary<string, string> { ["invoice_number"] = invoiceNumber },
            [pdf],
            [sourceUrl]);

        result.SelectedArtifact!.Content.ToArray().Should().Equal(pdf.Content.ToArray());
        result.SelectedArtifact.ExpectedMatch.Should().BeTrue();
        result.SelectedArtifact.MatchReasonCode.Should().Be("invoice_number_from_url");
    }

    [Fact]
    public void Rejects_multiple_unmatched_pdfs_instead_of_picking_first()
    {
        var captures = new[]
        {
            Artifact(RecoveredArtifactKind.Pdf, "invoice_number", "100"),
            Artifact(RecoveredArtifactKind.Pdf, "invoice_number", "200"),
        };

        var act = () => DirectInvoiceArtifactSelector.Select(
            new Dictionary<string, string> { ["invoice_number"] = "300" }, captures);

        act.Should().Throw<UrlRecoveryException>()
            .Which.ReasonCode.Should().Be("DIRECT_INVOICE_PDF_ENTITY_MISMATCH");
    }

    [Fact]
    public void Rejects_single_pdf_when_seller_conflicts_with_expected_fields()
    {
        var pdf = Artifact(RecoveredArtifactKind.Pdf, "seller", "Different Company");

        var act = () => DirectInvoiceArtifactSelector.Select(
            new Dictionary<string, string> { ["seller"] = "Expected Company" },
            [pdf]);

        act.Should().Throw<UrlRecoveryException>()
            .Which.ReasonCode.Should().Be("DIRECT_INVOICE_PDF_ENTITY_MISMATCH");
    }

    [Fact]
    public void Rejects_case_only_seller_difference_like_python_direct_selector()
    {
        var pdf = Artifact(RecoveredArtifactKind.Pdf, "seller", "ACME LTD");

        var act = () => DirectInvoiceArtifactSelector.Select(
            new Dictionary<string, string> { ["seller"] = "Acme Ltd" },
            [pdf]);

        act.Should().Throw<UrlRecoveryException>()
            .Which.ReasonCode.Should().Be("DIRECT_INVOICE_PDF_ENTITY_MISMATCH");
    }

    private static CapturedUrlArtifact Artifact(RecoveredArtifactKind kind, string fieldName, string fieldValue)
        => Artifact(kind, new Dictionary<string, string> { [fieldName] = fieldValue });

    private static CapturedUrlArtifact Artifact(RecoveredArtifactKind kind, IReadOnlyDictionary<string, string> fields)
    {
        var bytes = kind switch
        {
            RecoveredArtifactKind.Pdf => Encoding.ASCII.GetBytes("%PDF-1.7 fixture"),
            RecoveredArtifactKind.Xml => Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />"),
            _ => Encoding.ASCII.GetBytes("synthetic-ofd"),
        };
        return new CapturedUrlArtifact(kind, kind == RecoveredArtifactKind.Pdf ? "application/pdf" : "application/xml",
            bytes, 0, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), null,
            fields, null, "CAPTURED");
    }

    private sealed record DirectSelectionFixtureSet(IReadOnlyList<DirectSelectionFixture> Cases);

    private sealed record DirectSelectionFixture(
        string CaseId,
        IReadOnlyDictionary<string, string> ExpectedFields,
        IReadOnlyList<DirectSelectionArtifact> Artifacts,
        int? ExpectedSelectedIndex,
        string ExpectedSelectedKind,
        string ExpectedMatchReason,
        string ExpectedFailure);

    private sealed record DirectSelectionArtifact(string Kind, IReadOnlyDictionary<string, string> Fields);
}