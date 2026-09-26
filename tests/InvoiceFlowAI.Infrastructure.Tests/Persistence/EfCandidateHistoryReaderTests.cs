using FluentAssertions;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class EfCandidateHistoryReaderTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public EfCandidateHistoryReaderTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exists_only_for_completed_non_retryable_processing_records()
    {
        await _fixture.ResetAsync();
        await using (var context = _fixture.CreateContext())
        {
            context.Documents.AddRange(
                NewSource("completed"),
                NewSource("retryable"),
                NewSource("incomplete"));
            context.Runs.AddRange(
                NewRun("run-completed"),
                NewRun("run-retryable"),
                NewRun("run-incomplete"));
            await context.SaveChangesAsync();
            context.DocumentProcessing.AddRange(
                NewRecord("completed", completedAtUtc: DateTimeOffset.UnixEpoch, retryable: false),
                NewRecord("retryable", completedAtUtc: DateTimeOffset.UnixEpoch, retryable: true),
                NewRecord("incomplete", completedAtUtc: null, retryable: false));
            await context.SaveChangesAsync();
        }

        await using var queryContext = _fixture.CreateContext();
        var reader = new EfCandidateHistoryReader(queryContext);

        (await reader.ExistsAsync(DocumentIdentity.Create("completed"), CancellationToken.None)).Should().BeTrue();
        (await reader.ExistsAsync(DocumentIdentity.Create("retryable"), CancellationToken.None)).Should().BeFalse();
        (await reader.ExistsAsync(DocumentIdentity.Create("incomplete"), CancellationToken.None)).Should().BeFalse();
        (await reader.ExistsAsync(DocumentIdentity.Create("missing"), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Finds_completed_artifact_by_opaque_source_group_key()
    {
        await _fixture.ResetAsync();
        await using (var context = _fixture.CreateContext())
        {
            var source = NewSource("artifact-id");
            source.ProviderGroupKey = "opaque-provider-group";
            context.Documents.Add(source);
            context.Runs.Add(NewRun("run-artifact-id"));
            await context.SaveChangesAsync();
            context.DocumentProcessing.Add(NewRecord("artifact-id", DateTimeOffset.UnixEpoch, retryable: false));
            await context.SaveChangesAsync();
        }

        await using var queryContext = _fixture.CreateContext();
        var reader = new EfCandidateHistoryReader(queryContext);

        (await reader.ExistsAsync(DocumentIdentity.Create("opaque-provider-group"), CancellationToken.None)).Should().BeTrue();
    }

    private static DocumentProcessingRow NewRecord(string id, DateTimeOffset? completedAtUtc, bool retryable)
        => new()
        {
            DocumentId = id,
            ProcessingRevision = 0,
            RunId = $"run-{id}",
            Sequence = 0,
            Stage = "extract",
            Status = completedAtUtc.HasValue ? "Resolved" : "Unresolved",
            ReasonCode = string.Empty,
            Retryable = retryable,
            Attempt = 1,
            MaxAttempts = 2,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            CompletedAtUtc = completedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        };

    private static DocumentSourceRow NewSource(string id)
        => new()
        {
            DocumentId = id,
            SourceKind = "attachment",
            SourceMessageUid = $"uid-{id}",
            SourceFileName = $"{id}.pdf",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };

    private static RunRow NewRun(string id)
        => new()
        {
            RunId = id,
            State = "Running",
            Stage = "extract",
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            LastEventSequence = 0,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
}