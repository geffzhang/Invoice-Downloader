using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class ManualReviewFolderOpenRpcHandler(IDesktopActionService actions)
    : TypedRpcHandler<RunFolderOpenRequest, DesktopActionResult>("run.manual-review.open", allowNullParams: true)
{
    protected override Task<DesktopActionResult> ExecuteAsync(RunFolderOpenRequest parameters, CancellationToken cancellationToken)
        => actions.OpenManualReviewFolderAsync(parameters?.RunId, cancellationToken);
}
