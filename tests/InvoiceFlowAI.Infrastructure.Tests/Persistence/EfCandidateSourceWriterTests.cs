using FluentAssertions;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class EfCandidateSourceWriterTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public EfCandidateSourceWriterTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upserts_selected_artifact_with_opaque_group_key_without_persisting_source_url()
    {
        await _fixture.ResetAsync();
        var candidate = new DocumentCandidate(
            DocumentIdentity.Create("artifact-identity"), 12, "correlation", "message-uid", "invoice.pdf",
            "application/pdf", 42, 0, "url", new Uri("https://provider.example/download?token=secret"));
        await using (var context = _fixture.CreateContext())
        {
            var writer = new EfCandidateSourceWriter(context);
            await writer.UpsertSelectedArtifactAsync(candidate, new string('a', 64),
                DocumentIdentity.Create("opaque-group-key"), CancellationToken.None);
        }

        await using var queryContext = _fixture.CreateContext();
        var source = await queryContext.Documents.FindAsync("artifact-identity");
        source.Should().NotBeNull();
        source!.ProviderGroupKey.Should().Be("opaque-group-key");
        source.ContentHash.Should().Be(new string('a', 64));
        source.SourceLocator.Should().Be("artifact-identity");
        source.SourceLocator.Should().NotContain("provider.example");
        source.SourceLocator.Should().NotContain("secret");
    }
}