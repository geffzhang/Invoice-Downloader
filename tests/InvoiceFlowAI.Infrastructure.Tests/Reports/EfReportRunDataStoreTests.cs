using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Reports;
using InvoiceFlowAI.Infrastructure.Tests.Persistence;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Reports;

public sealed class EfReportRunDataStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public EfReportRunDataStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Load_projects_only_run_invoices_reviews_and_persisted_terminal_counts()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRunLifecycleStore(context);
        var uowFactory = new EfUnitOfWorkFactory(context);
        await using (var createUow = await uowFactory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            await store.TryCreateAsync(NewRun("run-report"), createUow, CancellationToken.None);
            await createUow.CommitAsync(CancellationToken.None);
        }

        var completedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        var summary = new RunSummary(
            "run-report", RunTerminalStatus.PartialSuccess, "RUN_PARTIAL_SUCCESS",
            2, 1, 3, 4, 5, 6, 7, 8, 9,
            new Dictionary<string, int> { ["EXTRACTION_FAILED"] = 2, ["ARCHIVE_REJECTED"] = 1 },
            "reports/run-report/report.xlsx", "existing-hash", completedAt, true, Array.Empty<RunFailure>());
        await using (var terminalUow = await uowFactory.BeginAsync(TransactionPurpose.TerminalCommit, CancellationToken.None))
        {
            await store.UpdateTerminalStateAsync(
                new RunStateSnapshot("run-report", RunLifecycleState.PartialSuccess, "lifecycle",
                    summary.TerminalReasonCode, 1, completedAt, null, summary),
                terminalUow,
                CancellationToken.None);
            await terminalUow.CommitAsync(CancellationToken.None);
        }

        await using (var otherRunUow = await uowFactory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            await store.TryCreateAsync(NewRun("run-other"), otherRunUow, CancellationToken.None);
            await otherRunUow.CommitAsync(CancellationToken.None);
        }
        context.Documents.AddRange(
            NewDocument("doc-resolved"), NewDocument("doc-review"), NewDocument("doc-other"));
        await context.SaveChangesAsync(CancellationToken.None);
        context.DocumentProcessing.AddRange(
            NewProcessing("run-report", "doc-resolved", 1, "Resolved", ""),
            NewProcessing("run-report", "doc-review", 2, "ManualReview", "LOW_CONFIDENCE"),
            NewProcessing("run-other", "doc-other", 1, "Resolved", ""));
        await context.SaveChangesAsync(CancellationToken.None);
        context.Invoices.AddRange(
            NewInvoice("invoice-report", "doc-resolved", 1),
            NewInvoice("invoice-other", "doc-other", 1));
        await context.SaveChangesAsync(CancellationToken.None);
        context.ArchivedArtifacts.Add(new ArchivedArtifactRow
        {
            ArtifactId = "artifact-report", RunId = "run-report", DocumentId = "doc-resolved",
            ProcessingRevision = 1, Role = "Standalone", RelativePath = "archive/run-report/invoice.pdf",
            FileName = "invoice.pdf", ContentHash = "artifact-hash", State = "Committed",
            CreatedAtUtc = completedAt, CommittedAtUtc = completedAt,
        });
        await context.SaveChangesAsync(CancellationToken.None);
        context.ManualReviewItems.Add(new ManualReviewItemRow
        {
            ReviewId = "review-1", RunId = "run-report", DocumentId = "doc-review", ProcessingRevision = 1,
            ReasonCode = "LOW_CONFIDENCE", State = "Open", CurrentRevision = 0,
            CreatedAtUtc = completedAt,
        });
        await context.SaveChangesAsync(CancellationToken.None);

        var data = await new EfReportRunDataStore(context).LoadAsync("run-report", CancellationToken.None);

        data.Should().NotBeNull();
        data!.Invoices.Should().ContainSingle();
        data.Invoices[0].InvoiceId.Should().Be("invoice-report");
        data.Invoices[0].Amount.Should().Be(100m);
        data.Invoices[0].CandidateStatus.Should().Be("Resolved");
        data.Invoices[0].RelativePath.Should().Be("archive/run-report/invoice.pdf");
        data.Invoices[0].ArchiveState.Should().Be("Committed");
        data.DateFrom.Should().Be(new DateOnly(2026, 9, 1));
        data.DateToExclusive.Should().Be(new DateOnly(2026, 10, 1));
        data.ManualReviews.Should().ContainSingle().Which.ReasonCode.Should().Be("LOW_CONFIDENCE");
        data.CandidateCounts.Should().Be(new ReportCandidateCounts(2, 1, 3, 4, 5, 6, 7, 8, 9));
        data.FailureReasonCounts.Should().BeEquivalentTo(summary.FailureReasonCounts);
        data.ExistingRelativePath.Should().Be("reports/run-report/report.xlsx");
        data.ExistingContentHash.Should().Be("existing-hash");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("run-active")]
    [InlineData("run-no-summary")]
    public async Task Load_returns_null_for_unknown_or_non_reportable_runs(string runId)
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        if (runId != "missing")
        {
            var lifecycle = new EfRunLifecycleStore(context);
            await using var uow = await new EfUnitOfWorkFactory(context)
                .BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None);
            await lifecycle.TryCreateAsync(NewRun(runId), uow, CancellationToken.None);
            if (runId == "run-no-summary")
            {
                var summary = new RunSummary(runId, RunTerminalStatus.Completed, "RUN_COMPLETED",
                    0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), null, null,
                    DateTimeOffset.UtcNow, false, Array.Empty<RunFailure>());
                await lifecycle.TryMarkRunningAsync(runId, "scan-mailbox", uow, CancellationToken.None);
                await uow.CommitAsync(CancellationToken.None);
                await using var terminalUow = await new EfUnitOfWorkFactory(context)
                    .BeginAsync(TransactionPurpose.TerminalCommit, CancellationToken.None);
                await lifecycle.UpdateTerminalStateAsync(
                    new RunStateSnapshot(runId, RunLifecycleState.Completed, "lifecycle", "RUN_COMPLETED",
                        1, summary.CompletedAtUtc, null), terminalUow, CancellationToken.None);
                await terminalUow.CommitAsync(CancellationToken.None);
            }
            else
            {
                await uow.CommitAsync(CancellationToken.None);
            }
        }

        var data = await new EfReportRunDataStore(context).LoadAsync(runId, CancellationToken.None);

        data.Should().BeNull();
    }

    [Fact]
    public async Task Save_updates_report_path_and_hash_in_terminal_summary()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var lifecycle = new EfRunLifecycleStore(context);
        var uowFactory = new EfUnitOfWorkFactory(context);
        await using (var createUow = await uowFactory.BeginAsync(TransactionPurpose.RunCreate, CancellationToken.None))
        {
            await lifecycle.TryCreateAsync(NewRun("run-save"), createUow, CancellationToken.None);
            await createUow.CommitAsync(CancellationToken.None);
        }

        var completedAt = DateTimeOffset.UtcNow;
        var summary = new RunSummary("run-save", RunTerminalStatus.Completed, "RUN_COMPLETED",
            0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), null, null,
            completedAt, false, Array.Empty<RunFailure>());
        await using (var terminalUow = await uowFactory.BeginAsync(TransactionPurpose.TerminalCommit, CancellationToken.None))
        {
            await lifecycle.UpdateTerminalStateAsync(
                new RunStateSnapshot("run-save", RunLifecycleState.Completed, "lifecycle", "RUN_COMPLETED",
                    1, completedAt, null, summary), terminalUow, CancellationToken.None);
            await terminalUow.CommitAsync(CancellationToken.None);
        }

        await using (var reportUow = await uowFactory.BeginAsync(TransactionPurpose.SettingsUpdate, CancellationToken.None))
        {
            await new EfReportRunDataStore(context).SaveAsync(
                "run-save", "reports/run-save/report.xlsx", "sha256-value", reportUow, CancellationToken.None);
            await reportUow.CommitAsync(CancellationToken.None);
        }
        var saved = await new EfReportRunDataStore(context).LoadAsync("run-save", CancellationToken.None);
        saved!.ExistingRelativePath.Should().Be("reports/run-save/report.xlsx");
        saved.ExistingContentHash.Should().Be("sha256-value");
    }

    [Theory]
    [InlineData("../escape.xlsx")]
    [InlineData("reports/../../escape.xlsx")]
    [InlineData("C:\\outside\\report.xlsx")]
    public async Task Save_rejects_report_paths_outside_the_output_root(string path)
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        await using var uow = await new EfUnitOfWorkFactory(context)
            .BeginAsync(TransactionPurpose.SettingsUpdate, CancellationToken.None);

        var act = () => new EfReportRunDataStore(context).SaveAsync(
            "run-1", path, "hash", uow, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
    private static RunCreationRequest NewRun(string runId) => new(
        runId, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), "account-1", 1,
        "INBOX", "C:\\Invoices", 1, "default", 1, "fingerprint", "1.0.0", DateTimeOffset.UtcNow);
    private static DocumentSourceRow NewDocument(string id) => new()
    {
        DocumentId = id, SourceKind = "email", SourceMessageUid = id, SourceFileName = id + ".pdf",
        SourceLocator = "mail:" + id, CreatedAtUtc = DateTimeOffset.UtcNow,
    };
    private static DocumentProcessingRow NewProcessing(string runId, string documentId, long sequence, string status, string reason) => new()
    {
        DocumentId = documentId, ProcessingRevision = 1, RunId = runId, Sequence = sequence,
        Stage = "archive-documents", Status = status, ReasonCode = reason, Retryable = false,
        Attempt = 1, MaxAttempts = 1, UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
    private static InvoiceRow NewInvoice(string id, string documentId, int revision) => new()
    {
        InvoiceId = id, DocumentId = documentId, ProcessingRevision = revision,
        InvoiceDate = new DateOnly(2026, 9, 15), Purchaser = "Buyer", Seller = "Seller",
        Amount = "100.00", TaxAmount = "13.00", TotalAmount = "113.00", InvoiceNumber = "INV-1",
        InvoiceCode = "CODE-1", DocumentType = "FlightInvoice", Category = "transport", Confidence = "0.98",
        ArchiveState = "Committed", Revision = 1,
    };
}
