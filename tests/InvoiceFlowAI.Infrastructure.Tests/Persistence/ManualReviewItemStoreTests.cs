using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class ManualReviewItemStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public ManualReviewItemStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unmatched_companions_get_separate_idempotent_open_review_rows()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.Runs.Add(new RunRow
        {
            RunId = "run-review",
            State = "Running",
            Stage = "pairing",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();
        foreach (var documentId in new[] { "invoice-anchor", "companion-a", "companion-b" })
        {
            context.Documents.Add(new DocumentSourceRow
            {
                DocumentId = documentId,
                SourceKind = "attachment",
                SourceFileName = $"{documentId}.pdf",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }
        await context.SaveChangesAsync();
        var sequence = 0L;
        foreach (var documentId in new[] { "invoice-anchor", "companion-a", "companion-b" })
        {
            context.DocumentProcessing.Add(new DocumentProcessingRow
            {
                DocumentId = documentId,
                ProcessingRevision = 4,
                RunId = "run-review",
                Sequence = sequence++,
                Stage = "pairing",
                Status = "NeedsManualReview",
                UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }
        await context.SaveChangesAsync();

        var store = new EfManualReviewItemStore(context);
        await using (var transaction = await BeginAsync(context))
        {
            await store.UpsertOpenAsync("run-review", "companion-a", 4, "COUNTERPART_MISSING", transaction, CancellationToken.None);
            await store.UpsertOpenAsync("run-review", "companion-b", 4, "NO_COMPATIBLE_EDGE", transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        await using (var transaction = await BeginAsync(context))
        {
            await store.UpsertOpenAsync("run-review", "companion-a", 4, "AMBIGUOUS_OPTIMUM", transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var rows = await context.ManualReviewItems.AsNoTracking().OrderBy(row => row.DocumentId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(row => row.DocumentId).Should().Equal("companion-a", "companion-b");
        rows.Select(row => row.ReasonCode).Should().Equal("AMBIGUOUS_OPTIMUM", "NO_COMPATIBLE_EDGE");
        rows.Should().OnlyContain(row => row.State == "Open" && row.DocumentId != "invoice-anchor");
    }

    private static async Task<IUnitOfWork> BeginAsync(InvoiceFlowDbContext context) =>
        await new EfUnitOfWorkFactory(context).BeginAsync(TransactionPurpose.PairingCommit, CancellationToken.None);
}