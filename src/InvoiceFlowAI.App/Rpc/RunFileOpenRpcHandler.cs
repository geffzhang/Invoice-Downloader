using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class RunFileOpenRpcHandler(IDesktopActionService actions)
    : TypedRpcHandler<RunFileOpenRequest, DesktopActionResult>("run.file.open")
{
    protected override Task<DesktopActionResult> ExecuteAsync(RunFileOpenRequest parameters, CancellationToken cancellationToken)
        => actions.OpenFileAsync(parameters, cancellationToken);
}
