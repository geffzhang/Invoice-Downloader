using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class RunAndReportRpcHandlersTests
{
    [Fact]
    public async Task Run_results_get_projects_persisted_invoice_rows()
    {
        var data = new ReportRunData(
            "run-1", "Completed", "RUN_COMPLETED", DateTimeOffset.UtcNow,
            [new ReportInvoiceRow("inv-1", new DateOnly(2026, 9, 15), "Buyer", "Seller", 100m, 13m, 113m,
                "INV-1", "CODE-1", "TaxInvoice", "travel", "0.95", "Committed")
            {
                CandidateStatus = "Resolved",
                RelativePath = "archive/run-1/invoice.pdf",
            }],
            [],
            new Dictionary<string, int>(),
            null,
            null,
            new ReportCandidateCounts(1, 0, 0, 0, 0, 0, 0, 0, 0));
        var query = new RunResultsQueryService(new FakeReportRunDataSource(data), new FakeDesktopRunService());
        var handler = new RunResultsGetRpcHandler(query);

        var result = await DispatchAsync(handler, new RunResultsRequest("run-1"));

        result.Error.Should().BeNull();
        result.Result!.Value.GetProperty("summary").GetProperty("success_count").GetInt32().Should().Be(1);
        result.Result.Value.GetProperty("successInvoices")[0].GetProperty("path")
            .GetString().Should().Be("archive/run-1/invoice.pdf");
    }

    [Fact]
    public async Task Report_export_serializes_success_and_hides_internal_exception_details()
    {
        var reports = new FakeReportApplicationService
        {
            ExportResult = new ReportExportRpcResult("run-1", "reports/run-1/report.xlsx", "hash", 2, 1, "1.0.0", false),
        };
        var handler = new ReportExportRpcHandler(reports);
        var success = await DispatchAsync(handler, new ReportExportRequest("run-1"));

        success.Error.Should().BeNull();
        success.Result!.Value.GetProperty("reportPath").GetString().Should().Be("reports/run-1/report.xlsx");
        success.Result.Value.GetProperty("invoiceRowCount").GetInt32().Should().Be(2);

        reports.ExportException = new InvalidOperationException("contains private filesystem details");
        var failure = await DispatchAsync(handler, new ReportExportRequest("run-1"));

        failure.Result.Should().BeNull();
        failure.Error!.Code.Should().Be(RpcErrorCodes.ReportExportFailed);
        failure.Error.UserMessage.Should().NotContain("private filesystem details");
    }

    private static Task<RpcHandlerResult> DispatchAsync<TParams, TResult>(
        TypedRpcHandler<TParams, TResult> handler,
        TParams parameters)
        where TParams : class
        => handler.HandleAsync(
            new RpcRequest<JsonElement?>(
                RpcDispatcher.Protocol,
                "test-request",
                handler.Method,
                JsonSerializer.SerializeToElement(parameters, JsonOptions.Default)),
            CancellationToken.None);

    private sealed class FakeDesktopRunService : IDesktopRunService
    {
        public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RunContextSnapshot(false, false, false, 0, null, null, null, null, null, null));

        public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeReportRunDataSource(ReportRunData data) : IReportRunDataSource
    {
        public Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<ReportRunData?>(runId == data.RunId ? data : null);
    }

    private sealed class FakeReportApplicationService : IReportApplicationService
    {
        public ReportExportRpcResult ExportResult { get; init; } = new("run-1", "report.xlsx", "hash", 0, 0, "1.0.0", false);
        public Exception? ExportException { get; set; }

        public Task<ReportExportRpcResult> ExportAsync(ReportExportRequest request, CancellationToken cancellationToken)
            => ExportException is null
                ? Task.FromResult(ExportResult)
                : Task.FromException<ReportExportRpcResult>(ExportException);

        public Task<ReportOpenToken> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<ReportOpenResolution> ResolveTokenAsync(string tokenId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
