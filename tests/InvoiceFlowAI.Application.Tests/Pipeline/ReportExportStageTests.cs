using FluentAssertions;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pipeline;

public sealed class ReportExportStageTests
{
    [Fact]
    public async Task Execute_exports_archive_rows_and_returns_report_metadata_with_terminal_counts()
    {
        var exporter = new FakeReportExporter();
        var stage = new ReportExportStage(exporter, new TerminalDecisionService(), TimeProvider.System);
        var resolved = NewResult(CandidateStatus.Resolved, includeInvoice: true);
        var manualReview = NewResult(CandidateStatus.ManualReview, reasonCode: "LOW_CONFIDENCE");
        var unresolved = NewResult(CandidateStatus.Unresolved, reasonCode: "DOWNLOAD_FAILED");
        var input = new PipelineItem<ArchiveBatch>(
            "run-1",
            new ArchiveBatch([resolved, manualReview, unresolved], Array.Empty<RunFailure>())
            {
                Artifacts = [new ArchiveArtifactOutcome(
                    resolved.Candidate.DocumentId.Value,
                    ArchiveArtifactState.Committed,
                    "2026/invoice.pdf",
                    null)],
            },
            1,
            OutputRoot: Path.GetTempPath());

        var summary = await stage.ExecuteAsync(input, CancellationToken.None);

        summary.ResolvedCount.Should().Be(1);
        summary.ManualReviewCount.Should().Be(1);
        summary.UnresolvedCount.Should().Be(1);
        summary.FailureReasonCounts.Should().ContainKey("DOWNLOAD_FAILED");
        summary.ReportPath.Should().Be("reports/run-1/report.xlsx");
        summary.ReportContentHash.Should().Be("content-hash");
        exporter.LastWorkItem.Should().NotBeNull();
        exporter.LastWorkItem!.Summary.ResolvedCount.Should().Be(1);
        exporter.LastWorkItem.Summary.ManualReviewCount.Should().Be(1);
        exporter.LastWorkItem.Invoices.Should().HaveCount(2);
        exporter.LastWorkItem.Invoices.Should().ContainSingle(row => row.DocumentType == "Failed");
        exporter.LastWorkItem.Invoices.Should().ContainSingle(row => row.ArchiveState == "Committed");
        exporter.LastWorkItem.ManualReviews.Should().ContainSingle()
            .Which.ReasonCode.Should().Be("LOW_CONFIDENCE");
        exporter.LastWorkItem.OutputRoot.Should().Be(Path.GetTempPath());
    }

    [Fact]
    public async Task Execute_propagates_export_failure_without_returning_success_summary()
    {
        var exporter = new FakeReportExporter { Exception = new IOException("disk full") };
        var stage = new ReportExportStage(exporter, new TerminalDecisionService(), TimeProvider.System);

        var act = () => stage.ExecuteAsync(
            new PipelineItem<ArchiveBatch>("run-1", new ArchiveBatch([], []), 1, OutputRoot: Path.GetTempPath()),
            CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Execute_honors_cancellation_before_export()
    {
        var exporter = new FakeReportExporter();
        var stage = new ReportExportStage(exporter, new TerminalDecisionService(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => stage.ExecuteAsync(
            new PipelineItem<ArchiveBatch>("run-1", new ArchiveBatch([], []), 1, OutputRoot: Path.GetTempPath()),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        exporter.LastWorkItem.Should().BeNull();
    }

    private static CandidateProcessResult NewResult(
        CandidateStatus status,
        bool includeInvoice = false,
        string? reasonCode = null)
    {
        var candidate = new DocumentCandidate(
            DocumentIdentity.Create(Guid.NewGuid().ToString("N")),
            1,
            "correlation",
            "message-1",
            "invoice.pdf",
            "application/pdf",
            128,
            1,
            "attachment");
        var invoice = includeInvoice
            ? new InvoiceDocument(
                candidate.DocumentId.Value,
                new DateOnly(2026, 9, 25),
                "Buyer",
                "Seller",
                100m,
                13m,
                113m,
                "CODE",
                "NUMBER",
                InvoiceDocumentType.TaxInvoice,
                "travel",
                null,
                Array.Empty<InvoiceItem>(),
                candidate.OriginalFileName,
                "sha256")
            : null;
        var failure = reasonCode is null
            ? null
            : new CandidateFailure(
                reasonCode,
                FailureScope.Candidate,
                FailureCategory.Validation,
                Retryable: false,
                SafeMessage: "Needs review");
        return new CandidateProcessResult(candidate, status, invoice, Failure: failure);
    }

    private sealed class FakeReportExporter : IReportExporter
    {
        public ReportExportWorkItem? LastWorkItem { get; private set; }
        public Exception? Exception { get; init; }

        public Task<ReportExportOutcome> ExportAsync(ReportExportWorkItem workItem, CancellationToken cancellationToken)
        {
            LastWorkItem = workItem;
            if (Exception is not null) throw Exception;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ReportExportOutcome(workItem.RelativePath, "content-hash", false));
        }
    }
}