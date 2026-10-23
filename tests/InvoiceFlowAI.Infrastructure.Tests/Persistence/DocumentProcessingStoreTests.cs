using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class DocumentProcessingStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public DocumentProcessingStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_creates_document_and_processing_rows_with_unique_run_sequences()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.Runs.Add(new RunRow
        {
            RunId = "run-processing",
            State = "Running",
            Stage = "archive-documents",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var uowFactory = new EfUnitOfWorkFactory(context);
        var store = new EfDocumentProcessingStore(context);
        var now = DateTimeOffset.UnixEpoch;

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await store.UpsertAsync(NewWrite("doc-a", "run-processing", now), transaction, CancellationToken.None);
            await store.UpsertAsync(NewWrite("doc-b", "run-processing", now), transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var documents = await context.Documents.AsNoTracking().OrderBy(row => row.DocumentId)
            .ToListAsync(TestContext.Current.CancellationToken);
        documents.Select(row => row.DocumentId).Should().Equal("doc-a", "doc-b");

        var rows = await context.DocumentProcessing.AsNoTracking().OrderBy(row => row.Sequence)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Select(row => row.Sequence).Should().Equal(0L, 1L);
        rows.Should().OnlyContain(row => row.RunId == "run-processing" && row.Status == "Unresolved");

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await store.UpsertAsync(
                NewWrite("doc-a", "run-processing", now) with { Status = CandidateStatus.ManualReview, ReasonCode = "URL_RECOVERY_FAILED" },
                transaction,
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var updated = await context.DocumentProcessing.AsNoTracking()
            .SingleAsync(row => row.DocumentId == "doc-a", TestContext.Current.CancellationToken);
        updated.Status.Should().Be("ManualReview");
        updated.ReasonCode.Should().Be("URL_RECOVERY_FAILED");
        (await context.DocumentProcessing.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        (await context.Documents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    private static DocumentProcessingWrite NewWrite(string documentId, string runId, DateTimeOffset now) => new(
        DocumentId: documentId,
        ProcessingRevision: 0,
        RunId: runId,
        SourceKind: "url",
        SourceMessageUid: "uid-1",
        SourceFileName: "invoice-url.pdf",
        Stage: "archive-documents",
        Status: CandidateStatus.Unresolved,
        ReasonCode: "URL_RECOVERY_FAILED",
        Retryable: true,
        Attempt: 1,
        MaxAttempts: 2,
        ArtifactPath: null,
        ResultJson: "{}",
        TraceJson: null,
        StartedAtUtc: now,
        CompletedAtUtc: now);
}
