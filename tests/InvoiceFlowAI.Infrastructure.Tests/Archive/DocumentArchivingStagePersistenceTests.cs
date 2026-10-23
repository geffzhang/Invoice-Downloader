using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class DocumentArchivingStagePersistenceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public DocumentArchivingStagePersistenceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Review_path_creates_processing_row_and_review_item_without_preseeded_processing()
    {
        await _fixture.ResetAsync();
        const string runId = "run-live-regression";
        var source = CreateSource("unrecovered.pdf", "unrecovered bytes");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"invoice-flow-live-{Guid.NewGuid():N}");
        try
        {
            await using var context = _fixture.CreateContext();
            context.Runs.Add(new RunRow
            {
                RunId = runId,
                State = "Running",
                Stage = "archive-documents",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var stage = CreateStage(context);
            var result = new CandidateProcessResult(
                new DocumentCandidate(
                    DocumentIdentity.Create("doc-live-review"),
                    Sequence: 0,
                    CorrelationId: "corr",
                    SourceMessageUid: "uid",
                    OriginalFileName: "unrecovered.pdf",
                    ContentType: "application/pdf",
                    ContentLength: 17,
                    ProcessingRevision: 0,
                    SourceKind: "url"),
                CandidateStatus.ManualReview,
                Invoice: null,
                ArtifactPath: source,
                Failure: new CandidateFailure(
                    "URL_RECOVERY_FAILED",
                    FailureScope.Candidate,
                    FailureCategory.Network,
                    Retryable: true,
                    SafeMessage: "Recovery failed."));

            var batch = await stage.ExecuteAsync(
                new ArchiveStageRequest(runId, outputRoot, new PairingBatch([], [result])),
                CancellationToken.None);

            var processed = batch.Results.Single();
            processed.Status.Should().Be(CandidateStatus.ManualReview);
            processed.Failure!.ReasonCode.Should().Be("URL_RECOVERY_FAILED");

            await using var verify = _fixture.CreateContext();
            var processing = await verify.DocumentProcessing.AsNoTracking()
                .SingleAsync(row => row.DocumentId == "doc-live-review", TestContext.Current.CancellationToken);
            processing.Status.Should().Be("ManualReview");
            processing.ReasonCode.Should().Be("URL_RECOVERY_FAILED");
            processing.RunId.Should().Be(runId);

            var review = await verify.ManualReviewItems.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            review.DocumentId.Should().Be("doc-live-review");
            review.ReasonCode.Should().Be("URL_RECOVERY_FAILED");
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Resolved_path_archives_and_persists_invoice_without_preseeded_processing()
    {
        await _fixture.ResetAsync();
        const string runId = "run-live-resolved";
        var source = CreateSource("invoice.pdf", "invoice bytes");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"invoice-flow-live-{Guid.NewGuid():N}");
        try
        {
            await using var context = _fixture.CreateContext();
            context.Runs.Add(new RunRow
            {
                RunId = runId,
                State = "Running",
                Stage = "archive-documents",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var stage = CreateStage(context);
            var invoice = new InvoiceDocument(
                DocumentId: "doc-live-resolved",
                InvoiceDate: new DateOnly(2026, 9, 24),
                Purchaser: "Buyer",
                Seller: "Merchant",
                Amount: 100m,
                TaxAmount: 13m,
                TotalAmount: 113m,
                InvoiceCode: "code",
                InvoiceNumber: "number",
                DocumentType: InvoiceDocumentType.RideInvoice,
                Category: null,
                Route: null,
                Items: Array.Empty<InvoiceItem>(),
                SourceFileName: "invoice.pdf",
                ContentHash: Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("invoice bytes")))
                    .ToLowerInvariant());
            var result = new CandidateProcessResult(
                new DocumentCandidate(
                    DocumentIdentity.Create("doc-live-resolved"),
                    Sequence: 0,
                    CorrelationId: "corr",
                    SourceMessageUid: "uid",
                    OriginalFileName: "invoice.pdf",
                    ContentType: "application/pdf",
                    ContentLength: 13,
                    ProcessingRevision: 0,
                    SourceKind: "attachment"),
                CandidateStatus.Resolved,
                invoice,
                ArtifactPath: source);

            var batch = await stage.ExecuteAsync(
                new ArchiveStageRequest(runId, outputRoot, new PairingBatch([], [result])),
                CancellationToken.None);

            batch.Artifacts.Should().ContainSingle()
                .Which.State.Should().Be(ArchiveArtifactState.Committed);

            await using var verify = _fixture.CreateContext();
            var processing = await verify.DocumentProcessing.AsNoTracking()
                .SingleAsync(row => row.DocumentId == "doc-live-resolved", TestContext.Current.CancellationToken);
            processing.Status.Should().Be("Resolved");

            (await verify.ArchivedArtifacts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

            var persisted = await verify.Invoices.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            persisted.InvoiceId.Should().Be("doc-live-resolved");
            persisted.TotalAmount.Should().Be("113");
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    private static DocumentArchivingStage CreateStage(InvoiceFlowDbContext context)
    {
        var uowFactory = new EfUnitOfWorkFactory(context);
        var fileSystem = new PhysicalArchiveFileSystem();
        return new DocumentArchivingStage(
            new ArchiveNamingPolicy(),
            new ArchiveCommitCoordinator(
                uowFactory,
                new EfArchiveArtifactStore(context),
                fileSystem,
                new EfAuditStore(context)),
            fileSystem,
            new EfPairingStore(context),
            new EfManualReviewItemStore(context),
            uowFactory,
            cwtCancellationFinalizer: null,
            processingStore: new EfDocumentProcessingStore(context),
            invoiceStore: new EfInvoiceStore(context));
    }

    private static string CreateSource(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"invoice-flow-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        return path;
    }
}
