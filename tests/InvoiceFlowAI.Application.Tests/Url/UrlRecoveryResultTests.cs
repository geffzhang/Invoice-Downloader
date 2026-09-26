using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Url;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Url;

public sealed class UrlRecoveryResultTests
{
    [Fact]
    public void Selected_artifact_index_must_reference_an_accepted_capture()
    {
        var pdf = Artifact(RecoveredArtifactKind.Pdf, "%PDF-1.7\nfixture", "application/pdf");

        var act = () => new UrlRecoveryResult([pdf], 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Empty_capture_inventory_cannot_select_an_artifact()
    {
        var result = new UrlRecoveryResult(Array.Empty<CapturedUrlArtifact>(), null);

        result.Artifacts.Should().BeEmpty();
        result.SelectedArtifact.Should().BeNull();
    }

    [Fact]
    public void Selected_artifact_is_exposed_without_expanding_other_captures()
    {
        var pdf = Artifact(RecoveredArtifactKind.Pdf, "%PDF-1.7\nfixture", "application/pdf");
        var xml = Artifact(RecoveredArtifactKind.Xml, "<?xml version=\"1.0\"?><invoice />", "application/xml");
        var result = new UrlRecoveryResult([pdf, xml], 0);

        result.SelectedArtifact.Should().BeSameAs(pdf);
        result.Artifacts.Should().HaveCount(2);
    }

    private static CapturedUrlArtifact Artifact(RecoveredArtifactKind kind, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new CapturedUrlArtifact(
            kind,
            contentType,
            bytes,
            0,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            "https://files.fixture.invalid",
            new Dictionary<string, string>(),
            null,
            "NO_EXPECTED_FIELDS");
    }
}