using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public class WindowCommandRpcHandler(
    string method,
    string command,
    IDesktopActionService actions)
    : TypedRpcHandler<RpcEmptyParams, DesktopActionResult>(method, allowNullParams: true)
{
    protected override Task<DesktopActionResult> ExecuteAsync(RpcEmptyParams parameters, CancellationToken cancellationToken)
        => actions.ExecuteWindowCommandAsync(command, cancellationToken);
}

public sealed class WindowMinimizeRpcHandler(IDesktopActionService actions)
    : WindowCommandRpcHandler("window.minimize", "minimize", actions);

public sealed class WindowMaximizeRpcHandler(IDesktopActionService actions)
    : WindowCommandRpcHandler("window.maximize", "maximize", actions);

public sealed class WindowCloseRpcHandler(IDesktopActionService actions)
    : WindowCommandRpcHandler("window.close", "close", actions);
