using FluentAssertions;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class RunResultsQueryServiceTests
{
    [Fact]
    public async Task GetAsync_projects_persisted_summary_invoices_and_reason_groups()
    {
        var source = new FakeReportRunDataSource(NewData());
        var runs = new FakeDesktopRunService("run-1");
        var service = new RunResultsQueryService(source, runs);

        var results = await service.GetAsync(null, CancellationToken.None);

        results.SuccessInvoices.Should().ContainSingle()
            .Which.Path.Should().Be("archive/run-1/invoice.pdf");
        results.Categories.Should().ContainKey("transport").WhoseValue.Should().Be(1);
        results.Summary["success_count"].Should().Be(2);
        results.Summary["manual_check_count"].Should().Be(5);
        results.Summary["retention_count"].Should().Be(4);
        results.ResultBreakdown["timeout"].Should().Be(10);
        results.ReasonCodeBreakdown.Should().ContainKey("LOW_CONFIDENCE").WhoseValue.Should().Be(3);
        results.ReasonCodeBreakdown.Should().ContainKey("DOWNLOAD_FAILED").WhoseValue.Should().Be(2);
        results.GroupedErrorInvoices.Should().ContainSingle(group => group.Key == "LOW_CONFIDENCE" && group.Count == 3)
            .Which.Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new RunErrorInvoiceResult(
                null, "LOW_CONFIDENCE", "Open", null, "archive/run-1/review/review.pdf"));
        results.GroupedErrorInvoices.Should().ContainSingle(group => group.Key == "DOWNLOAD_FAILED" && group.Count == 2)
            .Which.Items.Should().BeEmpty();
        results.ErrorInvoices.Should().ContainSingle();
        results.OutputPath.Should().Be("C:\\Invoices");
        results.ManualCheckPath.Should().Be(Path.Combine("C:\\Invoices", "archive", "run-1", "review"));
    }

    [Fact]
    public async Task GetAsync_returns_run_not_found_for_missing_or_non_reportable_run()
    {
        var service = new RunResultsQueryService(
            new FakeReportRunDataSource(null),
            new FakeDesktopRunService(null));

        Func<Task> act = () => service.GetAsync(null, CancellationToken.None);

        await act.Should().ThrowAsync<DesktopRunException>()
            .Where(exception => exception.ErrorCode == RpcErrorCodes.RunNotFound);
    }

    private static ReportRunData NewData() => new(
        "run-1",
        "PartialSuccess",
        "RUN_PARTIAL_SUCCESS",
        new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        [new ReportInvoiceRow("inv-1", new DateOnly(2026, 9, 15), "Buyer", "Seller", 100m, 13m, 113m,
            "INV-1", "CODE-1", "TaxInvoice", "transport", "0.95", "Committed")
        {
            CandidateStatus = "Resolved",
            RelativePath = "archive/run-1/invoice.pdf",
        }],
        [new ReportManualReviewRow("review-1", "doc-review-1", 1, "LOW_CONFIDENCE", "Open")
        {
            RelativePath = "archive/run-1/review/review.pdf",
        }],
        new Dictionary<string, int>
        {
            ["LOW_CONFIDENCE"] = 3,
            ["DOWNLOAD_FAILED"] = 2,
        },
        "reports/run-1/report.xlsx",
        "hash-1",
        new ReportCandidateCounts(2, 3, 4, 5, 6, 7, 8, 9, 10))
    {
        OutputRoot = "C:\\Invoices",
        DateFrom = new DateOnly(2026, 9, 1),
        DateToExclusive = new DateOnly(2026, 10, 1),
    };

    private sealed class FakeReportRunDataSource(ReportRunData? data) : IReportRunDataSource
    {
        public Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult(data?.RunId == runId ? data : null);
    }

    private sealed class FakeDesktopRunService(string? runId) : IDesktopRunService
    {
        public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RunContextSnapshot(false, false, false, 0, runId, null, null, null, null, null));
        public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<RunProgressSnapshot> GetProgressAsync(string? requestedRunId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
