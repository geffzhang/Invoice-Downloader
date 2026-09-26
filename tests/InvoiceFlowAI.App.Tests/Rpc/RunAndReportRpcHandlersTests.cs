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
    public async Task Run_start_forwards_typed_request_and_serializes_accepted_result()
    {
        var runs = new FakeDesktopRunService
        {
            StartResult = new RunStartResult(true, "run-1", null),
        };
        var handler = new RunStartRpcHandler(runs);
        var request = new RunStartRequest(
            "run-1", "account-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            "C:\\Invoices", "ACME", "full");

        var result = await DispatchAsync(handler, request);

        result.Error.Should().BeNull();
        result.Result!.Value.GetProperty("accepted").GetBoolean().Should().BeTrue();
        result.Result.Value.GetProperty("runId").GetString().Should().Be("run-1");
        runs.StartRequest.Should().BeEquivalentTo(request);
    }

    [Fact]
    public async Task Run_stop_maps_desktop_run_exception_to_stable_rpc_error()
    {
        var runs = new FakeDesktopRunService
        {
            StopException = new DesktopRunException(RpcErrorCodes.RunNotCancellable, "Run cannot be stopped."),
        };
        var handler = new RunStopRpcHandler(runs);

        var result = await DispatchAsync(handler, new RunStopRequest("run-1"));

        result.Result.Should().BeNull();
        result.Error!.Code.Should().Be(RpcErrorCodes.RunNotCancellable);
        result.Error.UserMessage.Should().Be("Run cannot be stopped.");
    }

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
        public RunStartResult StartResult { get; init; } = new(true, "run-1", null);
        public RunStopResult StopResult { get; init; } = new(true, false, null);
        public Exception? StopException { get; init; }
        public RunStartRequest? StartRequest { get; private set; }

        public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RunContextSnapshot(false, false, false, 0, null, null, null, null, null, null));

        public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
        {
            StartRequest = request;
            return Task.FromResult(StartResult);
        }

        public Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
            => StopException is null ? Task.FromResult(StopResult) : Task.FromException<RunStopResult>(StopException);
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
