using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class RunContextRpcHandler(IDesktopRunService runs)
    : TypedRpcHandler<RpcEmptyParams, RunContextSnapshot>("run.context.get", allowNullParams: true)
{
    protected override Task<RunContextSnapshot> ExecuteAsync(RpcEmptyParams parameters, CancellationToken cancellationToken)
        => runs.GetContextAsync(cancellationToken);
}

public sealed class RunStartRpcHandler(IDesktopRunService runs)
    : TypedRpcHandler<RunStartRequest, RunStartResult>("run.start")
{
    protected override Task<RunStartResult> ExecuteAsync(RunStartRequest parameters, CancellationToken cancellationToken)
        => runs.StartAsync(parameters, cancellationToken);
}

public sealed class RunStatusRpcHandler(IDesktopRunService runs)
    : TypedRpcHandler<RunProgressRequest, RunProgressSnapshot>("run.progress.get", allowNullParams: true)
{
    protected override Task<RunProgressSnapshot> ExecuteAsync(RunProgressRequest parameters, CancellationToken cancellationToken)
        => runs.GetProgressAsync(parameters?.RunId, cancellationToken);
}

public sealed class RunStopRpcHandler(IDesktopRunService runs)
    : TypedRpcHandler<RunStopRequest, RunStopResult>("run.stop")
{
    protected override Task<RunStopResult> ExecuteAsync(RunStopRequest parameters, CancellationToken cancellationToken)
        => runs.StopAsync(parameters, cancellationToken);
}

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
