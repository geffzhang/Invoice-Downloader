using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Infrastructure.Url;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Url;

public sealed class BaiwangArtifactSelectorTests
{
    [Fact]
    public void Selects_pdf_from_same_source_as_matching_xml_and_skips_wrapper_pdf()
    {
        var wrapper = Artifact(RecoveredArtifactKind.Pdf, 0, new Dictionary<string, string>(), "BAIWANG_WRAPPER_DETECTED");
        var xml = Artifact(RecoveredArtifactKind.Xml, 1,
            new Dictionary<string, string> { ["invoice_number"] = "12345678" });
        var relatedPdf = Artifact(RecoveredArtifactKind.Pdf, 1, new Dictionary<string, string>());
        var unrelatedPdf = Artifact(RecoveredArtifactKind.Pdf, 2, new Dictionary<string, string>());

        var result = BaiwangArtifactSelector.Select(
            new Dictionary<string, string> { ["invoice_number"] = "12345678" },
            [wrapper, xml, relatedPdf, unrelatedPdf]);

        result.SelectedArtifact!.SourceUrlOrdinal.Should().Be(1);
        result.SelectedArtifact.Kind.Should().Be(RecoveredArtifactKind.Pdf);
        result.SelectedArtifact.MatchReasonCode.Should().Be("xml_then_pdf_same_source");
    }

    [Fact]
    public void Selects_first_non_wrapper_pdf_when_no_expected_fields_exist()
    {
        var wrapper = Artifact(RecoveredArtifactKind.Pdf, 0, new Dictionary<string, string>(), "BAIWANG_WRAPPER_DETECTED");
        var invoice = Artifact(RecoveredArtifactKind.Pdf, 1, new Dictionary<string, string>());

        var result = BaiwangArtifactSelector.Select(new Dictionary<string, string>(), [wrapper, invoice]);

        result.SelectedArtifact!.SourceUrlOrdinal.Should().Be(1);
        result.SelectedArtifact.ExpectedMatch.Should().BeTrue();
        result.SelectedArtifact.MatchReasonCode.Should().Be("matched_pdf");
    }

    [Fact]
    public void Rejects_multiple_unmatched_pdfs_instead_of_selecting_first()
    {
        var first = Artifact(RecoveredArtifactKind.Pdf, 0,
            new Dictionary<string, string> { ["invoice_number"] = "100" });
        var second = Artifact(RecoveredArtifactKind.Pdf, 1,
            new Dictionary<string, string> { ["invoice_number"] = "200" });

        var act = () => BaiwangArtifactSelector.Select(
            new Dictionary<string, string> { ["invoice_number"] = "300" },
            [first, second]);

        act.Should().Throw<UrlRecoveryException>()
            .Which.ReasonCode.Should().Be("BAIWANG_PDF_ENTITY_MISMATCH");
    }

    [Fact]
    public void Rejects_one_decimal_amount_that_python_normalizes_as_an_integer_amount()
    {
        var pdf = Artifact(RecoveredArtifactKind.Pdf, 0,
            new Dictionary<string, string> { ["amount"] = "12.3" });

        var act = () => BaiwangArtifactSelector.Select(
            new Dictionary<string, string> { ["amount"] = "12.30" },
            [pdf]);

        act.Should().Throw<UrlRecoveryException>()
            .Which.ReasonCode.Should().Be("BAIWANG_PDF_ENTITY_MISMATCH");
    }

    private static CapturedUrlArtifact Artifact(
        RecoveredArtifactKind kind,
        int ordinal,
        IReadOnlyDictionary<string, string> fields,
        string matchReason = "CAPTURED")
    {
        var content = kind == RecoveredArtifactKind.Pdf
            ? Encoding.ASCII.GetBytes("%PDF-1.7 fixture")
            : Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice />");
        return new CapturedUrlArtifact(kind, "application/" + kind.ToString().ToLowerInvariant(), content,
            ordinal, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), "https://pis.baiwang.com",
            fields, null, matchReason);
    }
}