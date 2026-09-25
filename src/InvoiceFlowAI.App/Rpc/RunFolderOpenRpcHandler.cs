using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class RunFolderOpenRpcHandler(IDesktopActionService actions)
    : TypedRpcHandler<RunFolderOpenRequest, DesktopActionResult>("run.folder.open", allowNullParams: true)
{
    protected override Task<DesktopActionResult> ExecuteAsync(RunFolderOpenRequest parameters, CancellationToken cancellationToken)
        => actions.OpenRunFolderAsync(parameters?.RunId, cancellationToken);
}
