using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class RunResultsGetRpcHandler(RunResultsQueryService results)
    : TypedRpcHandler<RunResultsRequest, RunResultsSnapshot>("run.results.get", allowNullParams: true)
{
    protected override Task<RunResultsSnapshot> ExecuteAsync(RunResultsRequest parameters, CancellationToken cancellationToken)
        => results.GetAsync(parameters?.RunId, cancellationToken);
}

public sealed class ReportExportRpcHandler(IReportApplicationService reports)
    : TypedRpcHandler<ReportExportRequest, ReportExportRpcResult>("run.report.export")
{
    protected override async Task<ReportExportRpcResult> ExecuteAsync(
        ReportExportRequest parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reports.ExportAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new DesktopRunException(RpcErrorCodes.ReportExportFailed, "The report could not be exported.");
        }
    }
}
